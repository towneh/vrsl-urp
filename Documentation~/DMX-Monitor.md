# DMX Monitor

A live view of every channel in a universe, shaded by value, read from whichever DMX
source is actually driving the scene.

**VRSL → URP → DMX Config → DMX Monitor.** It opens as an ordinary editor window, so it docks beside
the Inspector or tears off to float, whichever suits.

## What it's for

A lit scene hides most DMX faults. A patch off by one lights every fixture and moves
every head, just reading its neighbour's values, and that reads as a lighting design
decision rather than a fault. A universe that stopped arriving keeps its last values on
screen, so a static look and a dead feed are the same picture in any view of the values
alone. And a channel source that
registered but never heard anything looks exactly like no source at all.

The monitor separates those. It shows the values themselves, where they came from, and
how long ago each universe was last heard from.

It never writes a channel. That is deliberate — a diagnostic that can change the signal
can be the cause of the fault it is being used to chase.

## Play mode only

Neither light manager has `[ExecuteAlways]`, so no channel buffer is uploaded and no CRT
chain runs while the scene is stopped. The window says so rather than presenting an empty
grid as a finding.

## Reading the header

The top box names the path the fixtures are really reading:

| Header | Means |
|---|---|
| `Channel buffer — <source type>` | A source is publishing bytes and the fixtures read them. |
| `Video grid — CRT decode chain` | No channel source. The pixel grid drives the fixtures. |
| A warning naming a registered source | The source is registered but publishing no universes, so the manager stopped publishing and the fixtures fell back to the grid. |

That last one is the state worth having a tool for. From the lights it is indistinguishable
from having no source in the scene at all.

## The views

**Fixture** — 13 wide by 40 rows, VRSL's own packing. One row is one 13-channel fixture,
so a patch off by one shears diagonally across the whole page and is obvious at a glance.

**Desk** — 32 wide by 16 rows. Channel numbers land on round boundaries, which makes a
specific channel findable by eye.

**Overview** — every universe at once, one row each. Use it to see which universes are
live before picking one to look at.

The last eight addresses of each universe are padding: the decode grid is 13 wide, 512
does not divide by 13, so each universe is rounded up to 40 whole rows and the next one
starts on a fresh row. No desk can address those and nothing reads them. They are drawn
in a distinct colour so a zero there isn't mistaken for a dark fixture.

## The ramps

**Heat** is monotonic in lightness, so the colours read as an ordering rather than as
categories, and zero sits obviously dark.

**Grey** maps the byte straight to a grey level. Use it when comparing against a desk.

**Change** shades each cell by how much it moved since the last sample rather than by its
value. It detects channel *movement*, not arrival: a source republishing the same values
every frame is dark here, exactly like one that has stopped sending. For liveness read the
`last heard N s ago` figure in the status bar instead, which is driven by each universe's
own latch time and keeps climbing whether or not the values move.

## Hovering a cell

The status bar names the universe and channel, the flat VRSL address, the value as both a
byte and a normalised float, and — when a fixture is patched there — that fixture and what
the channel does for it. The role comes from the fixture's own layout, so a 5-channel
static reports `dimmer / red / green / blue / strobe` and a 13-channel fixture reports the
full pan / tilt / zoom / dimmer / strobe / RGB / gobo / smoothing set.

Both `VRStageLighting_DMX_RealtimeLight` and `VRStageLighting_DMX_Static` are scanned. The
list is rebuilt when the hierarchy changes, not per frame.

## Verify

On the buffer path only, **Verify** additionally reads the channels back through the
compute shader and compares them against what the source published. It catches a packing
or indexing fault against live data, where
`VRSL → URP → DMX Config → Validate DMX Channel Buffer` only checks against the synthetic source's Ramp
pattern.

It costs a dispatch and a readback per sample, so it is off by default.

A constant offset in the reported channel is an indexing fault. A value that looks like a
neighbouring byte is a packing fault.

## Cost

Nothing runs while the window is shut.

While it is open, values are sampled at 30 Hz. On the buffer path with `Verify` off that is
a read of an array the manager already keeps, with no GPU work at all; `Verify` adds one
compute dispatch and one asynchronous readback per sample, which is why it is opt-in. On the
video path it is always one dispatch of the same accessor the fixtures use, read back
asynchronously.

A window docked behind another tab parks itself: it only asks to be repainted while it is
being drawn, and switching back to the tab draws it once unprompted, which restarts it.

## Limitation on the video path

Channel values are read back through the compute shader's `IndustryRead` decode, which is
the same accessor the render-pass lights use. That path does not implement legacy
compatibility mode or nine-universe mode, so a grid authored either way will not read
correctly in the monitor. The window says so under the header whenever it is on the video
path.

The buffer path has no such limitation — the bytes are read directly.
