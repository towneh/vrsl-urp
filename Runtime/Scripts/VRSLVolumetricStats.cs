using System;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// One frame of counters from the volumetric raymarch.
    /// </summary>
    /// <remarks>
    /// The march writes these only on request, because collecting them is one
    /// atomic per counter per pixel and a frame that collects is not a frame
    /// anyone should time. They are what says whether the adaptive step count
    /// and the visibility bound are doing anything: the image cannot, since
    /// both are designed to leave it alone.
    /// </remarks>
    public readonly struct VRSLVolumetricStats
    {
        /// <summary>False until a frame has been collected.</summary>
        public readonly bool Valid;
        /// <summary><c>Time.frameCount</c> when the counters were read back,
        /// one frame after the march that wrote them.</summary>
        public readonly int  Frame;
        /// <summary>Half-resolution pixels that reached the light loop, which is
        /// every pixel with a surface behind it, both eyes in stereo.</summary>
        public readonly long Pixels;
        /// <summary>Lights that were stepped, summed over those pixels.</summary>
        public readonly long LightsMarched;
        /// <summary>Steps taken, summed over those lights.</summary>
        public readonly long Steps;
        /// <summary>Lights the visibility bound skipped before any stepping.</summary>
        public readonly long LightsSkipped;

        public VRSLVolumetricStats(int frame, uint[] words)
        {
            Valid         = true;
            Frame         = frame;
            Pixels        = words[0];
            LightsMarched = words[1];
            Steps         = words[2];
            LightsSkipped = words[3];
        }

        /// <summary>Average steps per light that was marched. The figure V11 is
        /// judged on: it falls as a cone fills less of the ray.</summary>
        public float StepsPerLight  => LightsMarched > 0 ? (float)Steps / LightsMarched : 0f;
        /// <summary>Average lights marched per pixel.</summary>
        public float LightsPerPixel => Pixels > 0 ? (float)LightsMarched / Pixels : 0f;
        /// <summary>Share of the lights that reached the loop which the bound
        /// skipped, 0 to 1.</summary>
        public float SkippedFraction
        {
            get
            {
                long considered = LightsMarched + LightsSkipped;
                return considered > 0 ? (float)LightsSkipped / considered : 0f;
            }
        }
    }

    /// <summary>
    /// The request, collect and read-back cycle for <see cref="VRSLVolumetricStats"/>,
    /// owned by a manager.
    /// </summary>
    /// <remarks>
    /// <see cref="Request"/> arms it. The next <see cref="Tick"/>, which each
    /// manager calls once per frame before its passes record, zeroes the buffer
    /// and raises <see cref="Collecting"/> for that frame's march. The tick after
    /// reads the buffer back, which stalls on the GPU and is the reason this is a
    /// request rather than a switch. Two frames from request to result.
    ///
    /// The buffer is always allocated while the manager runs and always bound by
    /// the pass, collecting or not: a UAV slot the shader declares is not
    /// uniformly safe to leave unbound across graphics APIs, and four words is
    /// nothing to hold.
    /// </remarks>
    public sealed class VRSLVolumetricStatsProbe : IDisposable
    {
        public const int Words = 4;

        static readonly uint[] s_zero = new uint[Words];
        readonly uint[] _read = new uint[Words];

        public GraphicsBuffer      Buffer     { get; private set; }
        public bool                Collecting { get; private set; }
        public VRSLVolumetricStats Last       { get; private set; }
        bool _armed;

        /// <summary>Ask for one frame of counters. Answered two ticks later in
        /// <see cref="Last"/>.</summary>
        public void Request() => _armed = true;

        public void Allocate()
        {
            Buffer ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, Words, sizeof(uint));
            Buffer.SetData(s_zero);
        }

        /// <summary>Once per frame, before the frame's passes record.</summary>
        public void Tick()
        {
            if (Buffer == null) return;
            if (Collecting)
            {
                Buffer.GetData(_read);
                Last       = new VRSLVolumetricStats(Time.frameCount, _read);
                Collecting = false;
            }
            if (_armed)
            {
                Buffer.SetData(s_zero);
                Collecting = true;
                _armed     = false;
            }
        }

        public void Dispose()
        {
            Buffer?.Release();
            Buffer     = null;
            Collecting = false;
            _armed     = false;
        }
    }
}
