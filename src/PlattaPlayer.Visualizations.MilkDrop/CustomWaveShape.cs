using System;
using System.Collections.Generic;
using PlattaPlayer.Visualizations.MilkDrop.Expressions;
using PlattaPlayer.Visualizations.MilkDrop.Presets;

namespace PlattaPlayer.Visualizations.MilkDrop;

/// <summary>The per-frame inputs a custom wave/shape reads: timing, audio bands, and the q-registers.</summary>
internal readonly struct BlockInputs
{
    public double Time { get; init; }
    public double Fps { get; init; }
    public double Frame { get; init; }
    public double Bass { get; init; }
    public double BassAtt { get; init; }
    public double Mid { get; init; }
    public double MidAtt { get; init; }
    public double Treb { get; init; }
    public double TrebAtt { get; init; }
    public double AspectX { get; init; }
    public double AspectY { get; init; }
    public IReadOnlyList<double> Q { get; init; }
}

/// <summary>
/// Geometry the renderer draws for one custom wave: a strip (or dot cloud) of points in clip space, each
/// with its own colour. Interleaved <c>[x, y, r, g, b, a]</c> so a single buffer upload feeds the GPU.
/// </summary>
public sealed class WaveOutput
{
    public float[] Vertices { get; private set; } = new float[64 * 6];
    public int PointCount { get; internal set; }
    public bool UseDots { get; internal set; }
    public bool Additive { get; internal set; }
    public bool Thick { get; internal set; }

    internal void Ensure(int floats)
    {
        if (Vertices.Length < floats) Vertices = new float[floats];
    }
}

/// <summary>
/// Geometry for one custom shape across all its instances: a triangle-list fill (centre+edge colours) and
/// a line-list border, both interleaved <c>[x, y, r, g, b, a]</c> in clip space.
/// </summary>
public sealed class ShapeOutput
{
    public float[] FillVertices { get; private set; } = new float[256 * 6];
    public int FillVertexCount { get; internal set; }
    public float[] BorderVertices { get; private set; } = new float[256 * 6];
    public int BorderVertexCount { get; internal set; }
    public bool Additive { get; internal set; }

    // Buffers are fully rewritten each frame, so a grow can discard the old contents.
    internal void EnsureFill(int floats)
    {
        if (FillVertices.Length < floats) FillVertices = new float[floats];
    }

    internal void EnsureBorder(int floats)
    {
        if (BorderVertices.Length < floats) BorderVertices = new float[floats];
    }
}

/// <summary>
/// One custom waveform slot of a preset. Owns its own <see cref="VariableTable"/> (so its <c>t1</c>..<c>t8</c>
/// and locals never collide with the main equations or the other custom blocks) while sharing the engine's
/// <c>gmegabuf</c>. Each frame it runs the per-frame code once, then the per-point code across the analysed
/// waveform (or spectrum), reading back each point's position and colour. Mirrors MilkDrop's custom-wave
/// pass; left/right channels are fed the same mono sample since the host analyses mono audio.
/// </summary>
internal sealed class CustomWave
{
    private readonly VariableTable _vars = new();
    private readonly EvalContext _ctx;
    private readonly EelProgram _frame;
    private readonly EelProgram _point;
    private readonly Slots _s;

    private readonly bool _enabled;
    private readonly bool _spectrum;
    private readonly int _samples;
    private readonly double _scaling;
    private readonly double _baseR, _baseG, _baseB, _baseA;

    public WaveOutput Output { get; } = new();

    public CustomWave(CustomCodeBlock block, Dictionary<int, double> globalMemory)
    {
        _ctx = new EvalContext(_vars, globalMemory);
        _s = Slots.Intern(_vars);

        var init = EelProgram.Compile(block.InitCode, _vars);
        _frame = EelProgram.Compile(block.FrameCode, _vars);
        _point = EelProgram.Compile(block.PointCode, _vars);

        _enabled = Value(block, "enabled", 0) != 0;
        _spectrum = Value(block, "bspectrum", 0) != 0;
        _samples = (int)Value(block, "samples", 512);
        _scaling = Value(block, "scaling", 1);
        Output.UseDots = Value(block, "busedots", 0) != 0;
        Output.Additive = Value(block, "badditive", 0) != 0;
        Output.Thick = Value(block, "bdrawthick", 0) != 0;
        _baseR = Value(block, "r", 1);
        _baseG = Value(block, "g", 1);
        _baseB = Value(block, "b", 1);
        _baseA = Value(block, "a", 1);

        init.Execute(_ctx);
    }

    public void ComputeFrame(in BlockInputs inp, ReadOnlySpan<float> waveform, ReadOnlySpan<float> spectrum)
    {
        Output.PointCount = 0;
        if (!_enabled || _point.IsEmpty) return;

        WriteInputs(_vars, _s, inp);
        _vars.Set(_s.R, _baseR); _vars.Set(_s.G, _baseG); _vars.Set(_s.B, _baseB); _vars.Set(_s.A, _baseA);
        _frame.Execute(_ctx);
        double fr = _vars.Get(_s.R), fg = _vars.Get(_s.G), fb = _vars.Get(_s.B), fa = _vars.Get(_s.A);

        var src = _spectrum ? spectrum : waveform;
        if (src.Length < 2) return;
        var n = Math.Clamp(_samples, 2, src.Length);

        Output.Ensure(n * 6);
        var v = Output.Vertices;

        for (var i = 0; i < n; i++)
        {
            var sample = i / (double)(n - 1);
            var value = src[(int)(sample * (src.Length - 1))] * _scaling;

            _vars.Set(_s.Sample, sample);
            _vars.Set(_s.Value1, value);
            _vars.Set(_s.Value2, value);
            // Each point starts from the per-frame colour so per-point code can tint individual points.
            _vars.Set(_s.R, fr); _vars.Set(_s.G, fg); _vars.Set(_s.B, fb); _vars.Set(_s.A, fa);
            _vars.Set(_s.X, 0.5); _vars.Set(_s.Y, 0.5);

            _point.Execute(_ctx);

            var o = i * 6;
            // MilkDrop x,y are 0..1 with y down; map to clip space (-1..1, y up).
            v[o] = (float)(_vars.Get(_s.X) * 2.0 - 1.0);
            v[o + 1] = (float)(1.0 - _vars.Get(_s.Y) * 2.0);
            v[o + 2] = (float)_vars.Get(_s.R);
            v[o + 3] = (float)_vars.Get(_s.G);
            v[o + 4] = (float)_vars.Get(_s.B);
            v[o + 5] = (float)Math.Clamp(_vars.Get(_s.A), 0.0, 1.0);
        }

        Output.PointCount = n;
    }

    private static double Value(CustomCodeBlock b, string key, double fallback)
        => b.Values.TryGetValue(key, out var x) ? x : fallback;

    internal static void WriteInputs(VariableTable vars, in Slots s, in BlockInputs inp)
    {
        vars.Set(s.Time, inp.Time); vars.Set(s.Fps, inp.Fps); vars.Set(s.Frame, inp.Frame);
        vars.Set(s.Bass, inp.Bass); vars.Set(s.BassAtt, inp.BassAtt);
        vars.Set(s.Mid, inp.Mid); vars.Set(s.MidAtt, inp.MidAtt);
        vars.Set(s.Treb, inp.Treb); vars.Set(s.TrebAtt, inp.TrebAtt);
        var q = inp.Q;
        if (q is not null)
            for (var i = 0; i < 32 && i < q.Count; i++) vars.Set(s.Q[i], q[i]);
    }

    /// <summary>Cached slot indices shared by custom waves and shapes (interned per block).</summary>
    internal readonly struct Slots
    {
        public readonly int Time, Fps, Frame, Bass, BassAtt, Mid, MidAtt, Treb, TrebAtt;
        public readonly int Sample, Value1, Value2, X, Y, R, G, B, A;
        public readonly int Rad, Ang, R2, G2, B2, A2, BorderR, BorderG, BorderB, BorderA, Sides, Instance;
        public readonly int[] Q;

        private Slots(VariableTable v)
        {
            Q = new int[32];
            for (var i = 0; i < 32; i++) Q[i] = v.Intern("q" + (i + 1));
            Time = v.Intern("time"); Fps = v.Intern("fps"); Frame = v.Intern("frame");
            Bass = v.Intern("bass"); BassAtt = v.Intern("bass_att");
            Mid = v.Intern("mid"); MidAtt = v.Intern("mid_att");
            Treb = v.Intern("treb"); TrebAtt = v.Intern("treb_att");
            Sample = v.Intern("sample"); Value1 = v.Intern("value1"); Value2 = v.Intern("value2");
            X = v.Intern("x"); Y = v.Intern("y");
            R = v.Intern("r"); G = v.Intern("g"); B = v.Intern("b"); A = v.Intern("a");
            Rad = v.Intern("rad"); Ang = v.Intern("ang");
            R2 = v.Intern("r2"); G2 = v.Intern("g2"); B2 = v.Intern("b2"); A2 = v.Intern("a2");
            BorderR = v.Intern("border_r"); BorderG = v.Intern("border_g");
            BorderB = v.Intern("border_b"); BorderA = v.Intern("border_a");
            Sides = v.Intern("sides"); Instance = v.Intern("instance");
        }

        public static Slots Intern(VariableTable v) => new(v);
    }
}

/// <summary>
/// One custom shape slot of a preset. Like <see cref="CustomWave"/> it owns its variable table and shares
/// gmegabuf. Each frame, for every instance, it seeds the shape's defaults, runs the per-frame code, then
/// emits an <c>n</c>-sided polygon as a triangle fan (centre colour → edge colour gradient) plus a
/// line-list border. Faithful to MilkDrop's custom-shape pass; the optional texture fill is not applied
/// (the shape is drawn flat-coloured, matching the host's current sampler-less colour path).
/// </summary>
internal sealed class CustomShape
{
    private readonly VariableTable _vars = new();
    private readonly EvalContext _ctx;
    private readonly EelProgram _frame;
    private readonly CustomWave.Slots _s;

    private readonly bool _enabled;
    private readonly int _instances;
    private readonly int _sides;
    private readonly double _x, _y, _rad, _ang;
    private readonly double _r, _g, _b, _a, _r2, _g2, _b2, _a2;
    private readonly double _borderR, _borderG, _borderB, _borderA;

    public ShapeOutput Output { get; } = new();

    public CustomShape(CustomCodeBlock block, Dictionary<int, double> globalMemory)
    {
        _ctx = new EvalContext(_vars, globalMemory);
        _s = CustomWave.Slots.Intern(_vars);

        var init = EelProgram.Compile(block.InitCode, _vars);
        _frame = EelProgram.Compile(block.FrameCode, _vars);

        _enabled = Value(block, "enabled", 0) != 0;
        _instances = Math.Clamp((int)Value(block, "num_inst", 1), 1, 1024);
        _sides = Math.Clamp((int)Value(block, "sides", 4), 3, 100);
        _x = Value(block, "x", 0.5); _y = Value(block, "y", 0.5);
        _rad = Value(block, "rad", 0.1); _ang = Value(block, "ang", 0);
        _r = Value(block, "r", 1); _g = Value(block, "g", 0); _b = Value(block, "b", 0); _a = Value(block, "a", 1);
        _r2 = Value(block, "r2", 0); _g2 = Value(block, "g2", 1); _b2 = Value(block, "b2", 0); _a2 = Value(block, "a2", 0);
        _borderR = Value(block, "border_r", 1); _borderG = Value(block, "border_g", 1);
        _borderB = Value(block, "border_b", 1); _borderA = Value(block, "border_a", 0.1);
        Output.Additive = Value(block, "additive", 0) != 0;

        init.Execute(_ctx);
    }

    public void ComputeFrame(in BlockInputs inp)
    {
        Output.FillVertexCount = 0;
        Output.BorderVertexCount = 0;
        if (!_enabled) return;

        var ax = inp.AspectX == 0 ? 1.0 : inp.AspectX;
        var ay = inp.AspectY == 0 ? 1.0 : inp.AspectY;

        // Worst-case capacity for all instances: fill = sides triangles*3 verts; border = sides segments*2.
        Output.EnsureFill(_instances * _sides * 3 * 6);
        Output.EnsureBorder(_instances * _sides * 2 * 6);
        var fill = Output.FillVertices;
        var border = Output.BorderVertices;
        var fi = 0;
        var bi = 0;

        Span<double> ex = stackalloc double[_sides + 1];
        Span<double> ey = stackalloc double[_sides + 1];

        for (var inst = 0; inst < _instances; inst++)
        {
            CustomWave.WriteInputs(_vars, _s, inp);
            _vars.Set(_s.Instance, inst);
            _vars.Set(_s.X, _x); _vars.Set(_s.Y, _y); _vars.Set(_s.Rad, _rad); _vars.Set(_s.Ang, _ang);
            _vars.Set(_s.Sides, _sides);
            _vars.Set(_s.R, _r); _vars.Set(_s.G, _g); _vars.Set(_s.B, _b); _vars.Set(_s.A, _a);
            _vars.Set(_s.R2, _r2); _vars.Set(_s.G2, _g2); _vars.Set(_s.B2, _b2); _vars.Set(_s.A2, _a2);
            _vars.Set(_s.BorderR, _borderR); _vars.Set(_s.BorderG, _borderG);
            _vars.Set(_s.BorderB, _borderB); _vars.Set(_s.BorderA, _borderA);

            _frame.Execute(_ctx);

            var sides = Math.Clamp((int)_vars.Get(_s.Sides), 3, _sides);
            var cx = _vars.Get(_s.X);
            var cy = _vars.Get(_s.Y);
            var rad = _vars.Get(_s.Rad);
            var ang = _vars.Get(_s.Ang);

            float ccr = (float)_vars.Get(_s.R), ccg = (float)_vars.Get(_s.G), ccb = (float)_vars.Get(_s.B);
            float cca = (float)Math.Clamp(_vars.Get(_s.A), 0.0, 1.0);
            float er = (float)_vars.Get(_s.R2), eg = (float)_vars.Get(_s.G2), eb = (float)_vars.Get(_s.B2);
            float ea = (float)Math.Clamp(_vars.Get(_s.A2), 0.0, 1.0);
            float brr = (float)_vars.Get(_s.BorderR), brg = (float)_vars.Get(_s.BorderG), brb = (float)_vars.Get(_s.BorderB);
            float bra = (float)Math.Clamp(_vars.Get(_s.BorderA), 0.0, 1.0);

            // Edge ring in clip space (aspect-corrected so the polygon stays regular), with wrap point.
            for (var k = 0; k <= sides; k++)
            {
                var t = ang + k / (double)sides * (2.0 * Math.PI);
                var px = cx + rad * Math.Cos(t) / ax;
                var py = cy + rad * Math.Sin(t) / ay;
                ex[k] = px * 2.0 - 1.0;
                ey[k] = 1.0 - py * 2.0;
            }
            var ndcCx = (float)(cx * 2.0 - 1.0);
            var ndcCy = (float)(1.0 - cy * 2.0);

            for (var k = 0; k < sides; k++)
            {
                // triangle: centre, edge k, edge k+1
                fi = Append(fill, fi, ndcCx, ndcCy, ccr, ccg, ccb, cca);
                fi = Append(fill, fi, (float)ex[k], (float)ey[k], er, eg, eb, ea);
                fi = Append(fill, fi, (float)ex[k + 1], (float)ey[k + 1], er, eg, eb, ea);

                if (bra > 0f)
                {
                    bi = Append(border, bi, (float)ex[k], (float)ey[k], brr, brg, brb, bra);
                    bi = Append(border, bi, (float)ex[k + 1], (float)ey[k + 1], brr, brg, brb, bra);
                }
            }
        }

        Output.FillVertexCount = fi / 6;
        Output.BorderVertexCount = bi / 6;
    }

    private static int Append(float[] buf, int i, float x, float y, float r, float g, float b, float a)
    {
        buf[i] = x; buf[i + 1] = y; buf[i + 2] = r; buf[i + 3] = g; buf[i + 4] = b; buf[i + 5] = a;
        return i + 6;
    }

    private static double Value(CustomCodeBlock b, string key, double fallback)
        => b.Values.TryGetValue(key, out var x) ? x : fallback;
}
