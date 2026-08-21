# DMX Channel Sources

How DMX reaches the fixtures as bytes rather than as a video frame, how to feed it
from a Basis media player carrying DMX inside the stream, and how to write a source
of your own.

## The two DMX paths

VRSL's original path is a **pixel grid**: a video frame encodes channel values as
colours, a CustomRenderTexture chain decodes it, and fixtures sample the result.
It exists because a video frame was the only way to get DMX into a VRChat world.

A **channel source** skips all of that. Where the values arrive as bytes already,
a component implementing `IVRSLDMXChannelSource` hands them to the light manager,
the manager scatters them into a GPU channel buffer, and fixtures read the buffer.
No grid, no capture camera, no CRT chain, and the precision loss of encoding bytes
into pixels and back is gone.

A scene with no channel source behaves exactly as before. Assigning one switches
the manager to the buffer for as long as it is assigned.

Which of the two is driving the fixtures at any moment, and what every channel is
reading, is what [`DMX-Monitor.md`](DMX-Monitor.md) covers.

## Feeding it from a Basis media player

Lighting data can ride *inside* a live video stream. [Truss](https://github.com/towneh/Truss)
takes Art-Net from a desk and stamps each frame's DMX snapshot into the H.264
stream as SEI user data as the video passes through an RTMP relay. The data is
part of the access unit, so it stays locked to the picture and arrives wherever
the video arrives. The Basis media player raises every such message at the moment
its frame is shown, and this package turns the ones that are Truss's into channel
values.

The pieces:

```
BasisMediaPlayer ──UserDataReceived──▶ BasisUserDataToVRSLDMX ──IVRSLDMXChannelSource──▶ VRSL_URPLightManager
   (raises every SEI user-data            (keeps Truss's UUID, verifies             (scatters the blocks into
    message, any UUID, at the              the record, hands over blocks)            the channel buffer)
    frame it belongs to)
```

### Setting it up

Needs `com.basis.mediaplayer` in the project. `com.cnlohr.cilbox` is optional: with
it the components are `[Cilboxable]` and can be used from sandboxed world scripts;
without it they are plain components for a scene-owned player.

1. A `BasisMediaPlayer` showing the stream, set up as for any other source.
2. `BasisUserDataToVRSLDMX` (menu: *VRSL-URP / Basis SEI DMX Source*) on any
   GameObject, with **Player** pointing at that player.
3. A `VRSL_URPLightManager` in the scene. The component registers itself as the
   manager's channel source when enabled and clears the registration when disabled.
4. Fixtures patched as usual. Universes are addressed the way a desk counts them:
   the stream's universe 0 is VRSL's universe 1.

Fields:

| Field | What it does |
| --- | --- |
| `Player` | The player whose stream carries the data. Can be assigned or swapped at runtime. |
| `minimumUniverses` | How many universes to size the buffer for before any data arrives. The count grows when a block names a higher universe, and each growth reallocates the manager's buffers, so set it to the show's size to avoid a resize on the first cue. |
| `logDrops` | Log the first record dropped for each reason. Off by default, because a damaged stream would otherwise log at frame rate. |

What to read when checking it works:

| Property | Meaning |
| --- | --- |
| `RecordsDecoded` | Records decoded and handed on since enable. Climbs one per video frame on a stream carrying one record per frame. |
| `RecordsDropped` | Records that failed a check and were not applied. |
| `LastResult` | Why the most recent record was dropped, or `Ok`. |
| `LastHeader` | The outer frame of the most recent decoded record: sequence, frame index, send time, carrier. |
| `UniverseCount` | What the manager is sized for right now. |

### Behaviour worth knowing

- **Values are absolute**, so a dropped or late record is corrected as soon as a
  later record carries the affected slots again; nothing accumulates. How soon that
  is depends on the sender. Truss's relay resends every universe it has heard in
  every record, so with it a dropped record costs one frame and a client joining
  mid-show is right within a frame. A sender that carries only the channels that
  changed needs to resend whole universes periodically, or a dropped change stays
  missing and a late joiner never sees channels that do not change again. The
  manager itself keeps only what it was told and starts from zero.
- **A damaged record is dropped, not applied.** Each record carries a CRC over
  everything in it; one that fails, or whose framing does not add up, is counted
  and ignored rather than lit. On a show, "arrived broken" and "did not arrive" are
  different faults, and `LastResult` says which.
- **Partial snapshots are fine.** A record may carry only the channels that changed.
  Blocks delivered between two frames accumulate, so nothing is lost to a busy frame,
  and the manager keeps every value it was last told.
- **Timing is the video's.** The player raises each message when the frame carrying
  it is shown, so the lighting tracks the picture through whatever delay the path
  adds, rather than running ahead of it.
- **Each block carries its own age**: how long before the record was sent that
  universe was last heard from. Universes are latched as their Art-Net packets
  arrive, and 44 Hz does not divide into a frame grid, so the manager uses the age
  per universe rather than assuming they were all sampled together.

### What survives the network

SEI rides inside the video elementary stream. It survives any path that copies that
stream through unchanged, which is what a remuxing CDN does (RTSP, MPEG-TS and RTMP
egress have all been measured carrying it intact, and this package's own end-to-end row
has run green against VRCDN's TS egress). It does **not** survive a
transcode: a path that re-encodes the video drops the lane entirely and silently,
because the picture keeps working. A remux that runs bitstream filters over the
video can also strip or rewrite it.

The same rule decides whether a **recording** keeps the lane. A recorder that copies
the stream (`ffmpeg -c copy`, a server's recording feature, a remuxed capture) keeps
every record, MP4 included: the MP4 sample holds the whole access unit, and the
player's MP4 demuxer hands the SEI to the same scan as the TS path. A recorder that
re-encodes (a capture of the screen, a "convert" step, an encoder recording its own
canvas) loses it like a transcoding CDN does.

So when nothing lights:

| Symptom | Likely cause |
| --- | --- |
| `RecordsDecoded` stays at 0, `RecordsDropped` at 0 | The lane is not in the stream at this end. Re-encoded somewhere on the path, or the relay is not injecting. `truss-detect` against the same URL tells you which. |
| `RecordsDropped` climbing, `LastResult` = `BadCrc` or `BadMagic` | Something on the path rewrote the bytes. A bitstream filter, or a relay bug. |
| `LastResult` = `BadPayloadMagic` | The records are Truss's but are probe records from a measurement run, not lighting data. |
| Decoded climbs, fixtures dark | The patch. Check `UniverseCount` covers the universes the desk is sending, and that fixtures are addressed in the right universe (desk universe 0 is VRSL universe 1). |

### The record

`VRSLTrussDmx`, in the core assembly, decodes the record and knows nothing about
Basis. The layout is Truss's: a `TRUSSDMX` frame (version, carrier, sequence, send
time, frame index, payload length, CRC-32 over everything before it) around a `DMXS`
payload of blocks, each `(universe, start slot, length, age µs, values)`, all
integers big-endian, carried as SEI `user_data_unregistered` under the UUID
`b1f0a7d4-9c3e-4a52-8f61-2d7c5e0b93a8`. Anything else that delivers the same bytes
can feed the same decoder.

## Choosing between them

Both lanes can ride the same stream, and in the same stream they arrive together:
measured through a real player, the records and the picture named the same frame,
no frames apart. So the choice is not about timing. It is about what the path
between the desk and the fixture is allowed to do to the data.

| | Records (SEI user data) | Pixel grid |
| --- | --- | --- |
| Fidelity | The bytes the desk sent. Measured exact on every channel | Lossy by construction, see below |
| Survives a transcode | No. The lane is dropped, silently | Yes. It is the picture |
| Needs player support | Yes, the player has to surface SEI user data | No, anything that hands you a texture |
| Integrity | CRC-32 per record. A damaged record is dropped and counted | None. A corrupt frame lights the rig |
| Capacity | Whatever fits the bitrate | 3 universes in a 1080p frame, horizontal mode |
| Costs picture | Nothing | A 1920x208 strip, a fifth of a 1080p frame |
| Costs bitrate | About 1.7 kB a frame for 3 full universes, near 400 kbit/s at 30 fps. Send only the channels that changed and it drops in proportion | Nothing extra, but the strip takes bits the picture would have had |
| Values reach fixtures | Straight into the channel buffer | Through the interpolation CRT, so damped and a frame behind |

### Where the grid loses data, precisely

Worth knowing before treating a grid reading as the value a desk sent.

**TV range does not hold 256 values.** A normal encode carries luma in 16 to 235,
which is 220 levels for 256 DMX values, so some values are not distinguishable
once encoded and a channel reads back up to a unit or two low. Tagging the stream
PC range round trips exactly, but only on a decoder that honours the tag, which is
worth establishing before relying on it.

**Colour space will curve the values if any stage disagrees.** The grid is data
wearing a picture's clothes. Sampling an sRGB source into a linear target in linear
colour space converts, and nothing converts back: a value of 104 arrives as 35, and
104 and 105 both arrive as 35, so no later stage can recover them. `BasisVideoRenderTextureOutput`
handles this for the target it writes. Any other route into the grid RT has to.

**Forty channels read their neighbour.** The texture accessor carries a correction
table for the 13th channel of a sector across five channel ranges, and those
channels read the value 13 below their own. It applies to the grid path only. If a
fixture's 13th channel matters to a show, that is the path it must not come down.

### So

Prefer the records where the path is yours end to end and you can establish it
carries them, which `truss-detect` against your own egress settles in one run. That
is the lane that delivers what the desk sent rather than an approximation of it,
and the only one that can tell you when it did not.

Prefer the grid where the path is not yours, where a CDN may re-encode, or where the
player cannot surface user data. It is what VRSL has always used and it goes
everywhere video goes.

Sending both is a reasonable answer for a stream that has to work on paths you do
not control. The manager reads whichever channel source is assigned and falls back
to the grid when there is none, so a consumer can prefer the records and drop to the
picture on a transcoding path without the fixtures noticing. [`DMX-Monitor.md`](DMX-Monitor.md)
names which of the two is live at any moment, which is the thing to check first when
the values look wrong rather than absent.

## Writing your own channel source

Any `MonoBehaviour` implementing `IVRSLDMXChannelSource` can drive the rig:

```csharp
public interface IVRSLDMXChannelSource
{
    int UniverseCount { get; }
    bool TryGetBlocks(out NativeArray<VRSLDMXBlock> blocks, out int blockCount,
                      out NativeArray<byte> values);
}
```

The manager calls `TryGetBlocks` once per frame. The contract:

- **Return `false` when nothing new arrived.** That means "keep what you have", not
  "stop publishing"; the manager holds the last value it was told for every slot.
  A source that re-hands the same blocks every frame would re-apply them, and with
  them their ages, for time that did not pass.
- **Blocks are runs of consecutive slots from one universe**, addressed the way a
  desk is: a 0-based universe and a 0-based slot within it. The manager applies
  VRSL's 520-slot stride itself, so a source never needs to know about it.
- **Both arrays are borrowed for the call.** The manager copies out of them during
  `TryGetBlocks` and never touches them afterwards, so the source may reuse or
  resize them the moment the call returns.
- **Values are absolute and idempotent.** A partial snapshot corrects the slots it
  covers and leaves the rest alone.
- **`UniverseCount` sizes the buffer.** Universes are 0-based indices and this is a
  count, so it must be `max(block.universe) + 1`: a source naming universes 0 and 1
  with a count of 1 has universe 1 dropped. It should not change frame to frame;
  every change reallocates.
- A block naming a universe at or above `UniverseCount` is dropped; a run passing
  slot 512 or the end of the values array is truncated. Both quietly unless the
  manager's debug logs are on.
- Register with `VRSL_URPLightManager.Instance.ChannelSource = this` in `OnEnable`,
  and clear it in `OnDisable` if it still points at you. The manager can come up
  after your component, so check again in `Update` if your source may enable first.

`VRSL_SyntheticDMXChannelSource` (`Runtime/Scripts/`) is the reference
implementation: a desk that is not there, generating patterns on the CPU. Its Ramp
pattern, where every channel holds a known function of its address, is what the
*VRSL → URP → Validate DMX Channel Buffer* menu compares against, which makes it the
quickest way to prove a new path end to end.
