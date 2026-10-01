using PlattaPlayer.Visualizations.Wmp;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Runs the GPU engine's CPU half headlessly and reports what it would hand to OpenGL.
///
/// The GPU visualizer renders black in the app while the CPU engine over the same audio produces plenty
/// of ink, and a shader that compiles tells us nothing about whether it is being GIVEN anything to draw.
/// <see cref="AlchemyGpuEngine"/> does all of its scheduling, parameter harvesting and geometry emission
/// on the CPU — only the rasterization is GL — so the entire pipeline up to the driver can be exercised
/// here. If no geometry comes out, the feedback field has nothing to seed it and would stay black no
/// matter how correct the warp shader is.
///
/// Usage: probe-gpu [--frames N] [--size WxH]
/// </summary>
internal static class ProbeGpu
{
    private sealed class CountingWriter : IGpuDrawWriter
    {
        public int Strips;
        public int Vertices;
        public int Discs;
        public int OpaqueVertices;
        public float MinX = float.MaxValue, MaxX = float.MinValue;
        public float MinY = float.MaxValue, MaxY = float.MinValue;

        public void BeginStrip() => Strips++;

        public void Vertex(float xPx, float yPx, int argb)
        {
            Vertices++;
            if ((argb & 0xFFFFFF) != 0) OpaqueVertices++;
            if (xPx < MinX) MinX = xPx;
            if (xPx > MaxX) MaxX = xPx;
            if (yPx < MinY) MinY = yPx;
            if (yPx > MaxY) MaxY = yPx;
        }

        public void Disc(float cxPx, float cyPx, float radiusPx, int fillArgb, int rimArgb) => Discs++;

        public void Reset()
        {
            Strips = Vertices = Discs = OpaqueVertices = 0;
            MinX = MinY = float.MaxValue;
            MaxX = MaxY = float.MinValue;
        }
    }

    public static int Run(string[] args)
    {
        var frames = ArgInt(args, "--frames", 200);
        var (w, h) = Capture.ArgSize(args, "--size", 640, 480);

        if (!File.Exists(HarnessPaths.SynthFile))
        {
            Console.Error.WriteLine($"probe-gpu: {HarnessPaths.SynthFile} not found — run 'synth' first.");
            return 2;
        }
        var audio = AudioFrameSet.Read(HarnessPaths.SynthFile);

        var engine = new AlchemyGpuEngine(new Random(12345));
        engine.Resize(w, h);
        var state = new AlchemyGpuState();
        var writer = new CountingWriter();
        var levels = new TimedLevels();

        long totalVerts = 0, totalDiscs = 0, totalStrips = 0;
        var framesWithGeometry = 0;
        var firstGeometryFrame = -1;

        Console.WriteLine($"driving AlchemyGpuEngine for {frames} frames at {w}x{h}");
        Console.WriteLine();
        Console.WriteLine("  frame  warpA  warpB  child  draws  strips  verts  discs   name");

        for (var i = 0; i < frames; i++)
        {
            var a = audio[Math.Min(i + 30, audio.Count - 1)];   // skip the leading silence
            a.Frequency0.CopyTo(levels.Frequency[0], 0);
            a.Frequency1.CopyTo(levels.Frequency[1], 0);
            a.Waveform0.CopyTo(levels.Waveform[0], 0);
            a.Waveform1.CopyTo(levels.Waveform[1], 0);
            levels.State = 2;

            writer.Reset();
            engine.Update(levels, 1000.0 / WmpFrameRate.WindowedIntervalMs, state, writer);

            totalVerts += writer.Vertices;
            totalDiscs += writer.Discs;
            totalStrips += writer.Strips;
            if (writer.Vertices > 0 || writer.Discs > 0)
            {
                framesWithGeometry++;
                if (firstGeometryFrame < 0) firstGeometryFrame = i;
            }

            if (i % 40 == 0)
                Console.WriteLine($"  {i,5}  {(int)state.WarpAKind,5}  {(int)state.WarpBKind,5}  " +
                                  $"{(int)state.WarpAChildKind,5}  {"",5}  {writer.Strips,6}  " +
                                  $"{writer.Vertices,5}  {writer.Discs,5}   {state.CurrentName}");
        }

        Console.WriteLine();
        Console.WriteLine($"  frames producing geometry : {framesWithGeometry}/{frames}" +
                          (firstGeometryFrame >= 0 ? $" (first at {firstGeometryFrame})" : ""));
        Console.WriteLine($"  total strips / vertices   : {totalStrips:N0} / {totalVerts:N0}");
        Console.WriteLine($"  total discs               : {totalDiscs:N0}");
        Console.WriteLine($"  last frame vertex bounds  : x {writer.MinX:0.#}..{writer.MaxX:0.#}  " +
                          $"y {writer.MinY:0.#}..{writer.MaxY:0.#}   (field is {w}x{h})");
        Console.WriteLine($"  last frame non-black verts: {writer.OpaqueVertices}/{writer.Vertices}");

        Console.WriteLine();
        if (totalVerts == 0 && totalDiscs == 0)
        {
            Console.WriteLine("probe-gpu: NO GEOMETRY AT ALL — the feedback field is never seeded, which is");
            Console.WriteLine("           exactly what a black visualizer looks like. The bug is on the CPU");
            Console.WriteLine("           side of the GPU engine, not in the shader.");
            return 1;
        }
        Console.WriteLine("probe-gpu: the engine is emitting geometry.");
        return 0;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }
}
