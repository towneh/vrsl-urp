#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Compares two rendered frames and says how far apart they are.
    ///
    /// The rows this serves are the ones a person is currently asked to eyeball —
    /// "identical image, worse frametime", "cone grain unchanged as the backing
    /// surface moves away". A number alone cannot distinguish a global brightness
    /// shift from one wrong beam, so every comparison can write an amplified
    /// difference image beside its metrics.
    ///
    /// Editor-only, and internal: it reads back off the GPU and writes PNGs, neither
    /// of which belongs in a shipped player.
    /// </summary>
    static class VRSLImageCompare
    {
        /// <summary>
        /// How far apart two images are.
        ///
        /// Both a maximum and a 99th percentile, because they fail differently. A
        /// single stray pixel moves the maximum and nothing else; a global shift
        /// moves the percentile and barely touches the maximum. Reporting one without
        /// the other invites the wrong conclusion from a real difference.
        /// </summary>
        public readonly struct Result
        {
            /// <summary>Largest absolute per-channel difference, 0..1.</summary>
            public readonly float Max;
            /// <summary>99th percentile of the same, which is what a real regression
            /// moves and a single hot pixel does not.</summary>
            public readonly float P99;
            public readonly float Mean;
            /// <summary>Pixels differing by more than <see cref="Threshold"/>.</summary>
            public readonly int   DifferingPixels;
            public readonly int   TotalPixels;
            public readonly bool  SizeMismatch;

            public Result(float max, float p99, float mean, int differing, int total, bool mismatch)
            {
                Max = max; P99 = p99; Mean = mean;
                DifferingPixels = differing; TotalPixels = total; SizeMismatch = mismatch;
            }

            /// <summary>
            /// Share of the frame that moved, and 100 when the two images are different
            /// sizes. Nothing was compared in that case, and zero would read to a caller
            /// judging on this alone as though nothing had changed.
            /// </summary>
            public float DifferingPercent => SizeMismatch ? 100f
                : TotalPixels > 0 ? 100f * DifferingPixels / TotalPixels : 0f;

            public override string ToString() => SizeMismatch
                ? "images are different sizes"
                : $"max {Max:F4}, p99 {P99:F4}, mean {Mean:F5}, "
                + $"{DifferingPixels} px differing ({DifferingPercent:F3}%)";
        }

        /// <summary>
        /// One 8-bit step, near enough. Two renders of the same scene are not
        /// bit-identical — the GPU is free to reorder floating-point work between
        /// draws — so an exact comparison fails on frames a person would call the
        /// same. This is the smallest difference that is not quantisation.
        /// </summary>
        public const float Threshold = 1.5f / 255f;

        /// <summary>
        /// Read a camera's target back into a texture.
        ///
        /// The camera must already have rendered this frame; this does not render it.
        /// Keeping the two apart matters because a row wants the frame at a specific
        /// frame index after warm-up, and rendering here would quietly add one.
        ///
        /// <para>The caller owns the returned texture and must <c>DestroyImmediate</c> it.
        /// Unity does not collect the native side, so a run that reads many frames holds
        /// every one of them for the rest of the editor session.</para>
        /// </summary>
        public static Texture2D Read(RenderTexture target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false);
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                texture.Apply();
                return texture;
            }
            finally { RenderTexture.active = previous; }
        }

        /// <summary>
        /// Compare two images channel by channel.
        ///
        /// The inputs are already 8-bit sRGB by the time they reach here, which is
        /// the perceptual space the comparison wants: differencing raw HDR values
        /// weights a change in a bright beam far above the same visible change in a
        /// dim one, so a threshold tuned for one is meaningless for the other.
        /// </summary>
        public static Result Compare(Texture2D a, Texture2D b)
        {
            if (a == null || b == null) throw new ArgumentNullException(a == null ? nameof(a) : nameof(b));
            if (a.width != b.width || a.height != b.height)
                return new Result(1f, 1f, 1f, 0, 0, true);

            var pixelsA = a.GetPixels32();
            var pixelsB = b.GetPixels32();
            int total = pixelsA.Length;

            // A histogram over the 256 possible 8-bit differences, rather than a list
            // of every pixel: a percentile over two million floats allocates far more
            // than the answer is worth, and the values are integers anyway.
            var histogram = new int[256];
            int max = 0;
            long sum = 0;
            int differing = 0;

            for (int i = 0; i < total; i++)
            {
                int dr = Mathf.Abs(pixelsA[i].r - pixelsB[i].r);
                int dg = Mathf.Abs(pixelsA[i].g - pixelsB[i].g);
                int db = Mathf.Abs(pixelsA[i].b - pixelsB[i].b);
                int d  = Mathf.Max(dr, Mathf.Max(dg, db));

                histogram[d]++;
                sum += d;
                if (d > max) max = d;
                if (d > Threshold * 255f) differing++;
            }

            // Rank at least one and at most the sample count. Truncating 0.99 * total
            // reaches zero on a small enough image, and the loop then returns the very
            // first bucket — reporting a 99th percentile of zero however different the
            // two images are.
            int p99 = 0;
            long target = Math.Min(total, Math.Max(1, (long)Math.Ceiling(total * 0.99)));
            long running = 0;
            for (int d = 0; d < 256; d++)
            {
                running += histogram[d];
                if (running >= target) { p99 = d; break; }
            }

            return new Result(max / 255f, p99 / 255f, (float)sum / total / 255f,
                              differing, total, false);
        }

        /// <summary>
        /// Write both inputs and an amplified difference beside them.
        ///
        /// Amplified because a real regression is often a handful of 8-bit steps, and
        /// an unamplified difference image is a black rectangle whatever went wrong.
        /// </summary>
        public static void WriteImages(string folder, string name,
                                       Texture2D expected, Texture2D actual, int amplification = 16)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"{name}-expected.png"), expected.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(folder, $"{name}-actual.png"),   actual.EncodeToPNG());

            if (expected.width != actual.width || expected.height != actual.height) return;

            var a = expected.GetPixels32();
            var b = actual.GetPixels32();
            var diff = new Color32[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                int dr = Mathf.Min(255, Mathf.Abs(a[i].r - b[i].r) * amplification);
                int dg = Mathf.Min(255, Mathf.Abs(a[i].g - b[i].g) * amplification);
                int db = Mathf.Min(255, Mathf.Abs(a[i].b - b[i].b) * amplification);
                diff[i] = new Color32((byte)dr, (byte)dg, (byte)db, 255);
            }

            var image = new Texture2D(expected.width, expected.height, TextureFormat.RGBA32, false, false);
            try
            {
                image.SetPixels32(diff);
                image.Apply();
                File.WriteAllBytes(Path.Combine(folder, $"{name}-diff-x{amplification}.png"),
                                   image.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }

        // ── Where reference images live ───────────────────────────────────────

        /// <summary>
        /// The programme repo's reference frames, or null.
        ///
        /// Located through <c>VRSL_PERF_HOME</c> so only the reference machine and any
        /// future CI take that path. A consuming project has no reason to hold one
        /// machine's golden frames, and rows that need them skip with a message rather
        /// than failing — a red row for an absent environment variable teaches people
        /// to ignore red rows.
        /// </summary>
        public static string GoldenFolder
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("VRSL_PERF_HOME");
                if (string.IsNullOrEmpty(home)) return null;
                string golden = Path.Combine(home, "golden");
                return Directory.Exists(golden) ? golden : null;
            }
        }

        /// <summary>
        /// Where the previous local capture of a row is kept.
        ///
        /// This is the default comparison, not the committed reference: two GPUs will
        /// not produce identical frames, so a committed image is a false-failure
        /// machine everywhere except where it was made. Comparing against this
        /// machine's own last capture is also what the tool is actually for — land a
        /// change, see what moved.
        /// </summary>
        public static string LocalFolder
        {
            get
            {
                string project = Directory.GetParent(Application.dataPath)!.FullName;
                return Path.Combine(project, "VRSL-Benchmarks", "images");
            }
        }

        public static string LocalPath(string name) => Path.Combine(LocalFolder, $"{name}.png");

        /// <summary>
        /// Load a stored image, or null when there is not one yet.
        ///
        /// <para>The caller owns the returned texture and must <c>DestroyImmediate</c> it.
        /// Unity does not collect the native side, so a run that reads many frames holds
        /// every one of them for the rest of the editor session.</para>
        /// </summary>
        public static Texture2D Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (texture.LoadImage(File.ReadAllBytes(path))) return texture;
            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        public static void Save(string path, Texture2D texture)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }

        // ── Deliberate faults, for proving the comparator ─────────────────────

        /// <summary>
        /// Shift an image by one pixel.
        ///
        /// A-M0-4 asks whether the comparison catches a one-pixel offset in the
        /// volumetric upsample. Seeding that fault in the shader would mean a debug
        /// keyword in shipped code and a variant to compile, for a test's benefit; the
        /// claim being made is about the comparator's sensitivity, and this exercises
        /// exactly that on real rendered content.
        ///
        /// <para>The caller owns the returned texture and must <c>DestroyImmediate</c> it.
        /// Unity does not collect the native side, so a run that reads many frames holds
        /// every one of them for the rest of the editor session.</para>
        /// </summary>
        public static Texture2D ShiftedByOnePixel(Texture2D source)
        {
            var pixels = source.GetPixels32();
            var shifted = new Color32[pixels.Length];
            int w = source.width, h = source.height;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    shifted[y * w + x] = pixels[y * w + Mathf.Min(w - 1, x + 1)];

            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            texture.SetPixels32(shifted);
            texture.Apply();
            return texture;
        }
    }
}
#endif
