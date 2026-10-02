using System;
using System.Collections.Generic;
using PlattaPlayer.Visualizations.MilkDrop.Audio;
using PlattaPlayer.Visualizations.MilkDrop.Expressions;
using PlattaPlayer.Visualizations.MilkDrop.Presets;

namespace PlattaPlayer.Visualizations.MilkDrop;

/// <summary>
/// Drives one MilkDrop preset: compiles its equation blocks, feeds the analysed audio and timing into
/// the shared variable table, runs the init/per-frame equations, and reads back the resulting
/// <see cref="FrameState"/> for the renderer. This is the platform-independent heart of the visualizer
/// — it never touches the GPU, so it can back any rendering head (desktop GL today, mobile later).
///
/// Phase 1 evaluates the per-frame block and exposes the global feedback transform; the per-pixel mesh
/// warp and HLSL warp/comp shaders are a later phase (the per-pixel source is parsed and held ready).
/// </summary>
public sealed class MilkdropEngine
{
    private readonly AudioProcessor _audio = new();
    private readonly Dictionary<int, double> _globalMemory = new();
    private readonly double[] _q = new double[32];      // q1..q32 after this frame's per-frame pass
    private readonly double[] _qInit = new double[32];  // snapshot after per-frame-init, restored each frame

    private VariableTable _vars = new();
    private EvalContext _ctx;
    private EelProgram _frameProgram = EelProgram.Empty;
    private EelProgram _pixelProgram = EelProgram.Empty;
    private MilkdropPreset? _preset;
    private WarpMesh _mesh = new(48, 36);
    private CustomWave[] _waves = [];
    private CustomShape[] _shapes = [];
    private WaveOutput[] _waveOutputs = [];
    private ShapeOutput[] _shapeOutputs = [];

    private Slots _slot;
    private double _presetStartTime;
    private int _frameCount;

    public MilkdropEngine() => _ctx = new EvalContext(_vars, _globalMemory);

    public MilkdropPreset? CurrentPreset => _preset;

    /// <summary>Latest analysed audio (waveform + bands); valid after each <see cref="Update"/>.</summary>
    public AudioFrame Audio => _audio.Frame;

    /// <summary>
    /// The preset's <c>q1</c>..<c>q32</c> registers as left by this frame's per-frame equations. MilkDrop
    /// presets compute these to drive pixel shaders (and custom waves/shapes); the rendering head feeds
    /// them to the translated GLSL as <c>q1</c>..<c>q32</c> uniforms. Index 0 is <c>q1</c>.
    /// </summary>
    public IReadOnlyList<double> QValues => _q;

    /// <summary>
    /// The per-pixel warp grid, with source UVs refreshed each <see cref="Update"/>. The rendering head
    /// draws this to sample the previous frame and produce the feedback warp.
    /// </summary>
    public WarpMesh Mesh => _mesh;

    /// <summary>
    /// The preset's custom waveforms, geometry refreshed each <see cref="Update"/> (empty for presets that
    /// define none). The rendering head draws each as a coloured line strip / dot cloud over the frame.
    /// </summary>
    public IReadOnlyList<WaveOutput> WaveOutputs => _waveOutputs;

    /// <summary>
    /// The preset's custom shapes, geometry refreshed each <see cref="Update"/> (empty for presets that
    /// define none). The rendering head draws each as a coloured triangle fan plus border lines.
    /// </summary>
    public IReadOnlyList<ShapeOutput> ShapeOutputs => _shapeOutputs;

    /// <summary>Mesh resolution used when the per-pixel warp is evaluated (MilkDrop default 48×36).</summary>
    public int MeshX { get; set; } = 48;
    public int MeshY { get; set; } = 36;

    public void Load(MilkdropPreset preset, double timeSeconds)
    {
        _preset = preset;
        _vars = new VariableTable();
        _ctx = new EvalContext(_vars, _globalMemory);
        _slot = Slots.Intern(_vars);

        if (_mesh.Cols != MeshX + 1 || _mesh.Rows != MeshY + 1)
            _mesh = new WarpMesh(MeshX, MeshY);

        var init = EelProgram.Compile(preset.InitCode, _vars);
        _frameProgram = EelProgram.Compile(preset.FrameCode, _vars);
        _pixelProgram = EelProgram.Compile(preset.PixelCode, _vars);

        _presetStartTime = timeSeconds;
        _frameCount = 0;

        // Seed timing/audio so init equations that read them get sane values, then run init once.
        WriteBuiltIns(0, 0, 60);
        SeedPerFrameDefaults(preset);
        init.Execute(_ctx);

        // Snapshot the q-registers the init pass produced; each frame restores them before per-frame runs.
        for (var i = 0; i < 32; i++) _qInit[i] = _vars.Get(_slot.Q[i]);

        // Build the custom wave/shape runtimes (each owns its own variable table, sharing gmegabuf).
        _waves = new CustomWave[preset.Waves.Count];
        _waveOutputs = new WaveOutput[preset.Waves.Count];
        for (var i = 0; i < _waves.Length; i++)
        {
            _waves[i] = new CustomWave(preset.Waves[i], _globalMemory);
            _waveOutputs[i] = _waves[i].Output;
        }

        _shapes = new CustomShape[preset.Shapes.Count];
        _shapeOutputs = new ShapeOutput[preset.Shapes.Count];
        for (var i = 0; i < _shapes.Length; i++)
        {
            _shapes[i] = new CustomShape(preset.Shapes[i], _globalMemory);
            _shapeOutputs[i] = _shapes[i].Output;
        }
    }

    /// <summary>
    /// Analyses <paramref name="pcm"/>, advances the preset by one frame, and returns the frame's
    /// motion/colour parameters. <paramref name="timeSeconds"/> is a monotonically increasing clock.
    /// </summary>
    public FrameState Update(ReadOnlySpan<float> pcm, int sampleCount, double timeSeconds, double fps)
    {
        var audio = _audio.Process(pcm, sampleCount);

        var time = timeSeconds - _presetStartTime;
        WriteBuiltIns(time, _frameCount, fps);
        _vars.Set(_slot.Bass, audio.Bass);
        _vars.Set(_slot.Mid, audio.Mid);
        _vars.Set(_slot.Treb, audio.Treb);
        _vars.Set(_slot.BassAtt, audio.BassAtt);
        _vars.Set(_slot.MidAtt, audio.MidAtt);
        _vars.Set(_slot.TrebAtt, audio.TrebAtt);

        if (_preset is not null) SeedPerFrameDefaults(_preset);
        for (var i = 0; i < 32; i++) _vars.Set(_slot.Q[i], _qInit[i]);
        _frameProgram.Execute(_ctx);
        _frameCount++;

        for (var i = 0; i < 32; i++) _q[i] = _vars.Get(_slot.Q[i]);
        var state = ReadFrameState();
        ComputeMesh(time);
        ComputeCustomBlocks(time, fps);
        return state;
    }

    // Runs each custom wave's per-frame/per-point code and each custom shape's per-frame code, refreshing
    // their geometry. Custom blocks read the q-registers and audio as inputs (never write them back).
    private void ComputeCustomBlocks(double time, double fps)
    {
        if (_waves.Length == 0 && _shapes.Length == 0) return;

        var audio = _audio.Frame;
        var inputs = new BlockInputs
        {
            Time = time,
            Fps = fps,
            Frame = _frameCount,
            Bass = audio.Bass, BassAtt = audio.BassAtt,
            Mid = audio.Mid, MidAtt = audio.MidAtt,
            Treb = audio.Treb, TrebAtt = audio.TrebAtt,
            AspectX = _vars.Get(_slot.AspectX),
            AspectY = _vars.Get(_slot.AspectY),
            Q = _q,
        };

        foreach (var wave in _waves) wave.ComputeFrame(inputs, audio.Waveform, audio.Spectrum);
        foreach (var shape in _shapes) shape.ComputeFrame(inputs);
    }

    /// <summary>
    /// Evaluates the per-pixel warp for every mesh vertex and writes the resulting source UVs into
    /// <see cref="Mesh"/>. Faithfully mirrors MilkDrop's <c>RunPerPixelEquations</c>: per vertex it exposes
    /// (x, y, rad, ang), resets the warp outputs to this frame's per-frame values, runs the per-pixel code,
    /// then folds zoom/stretch/warp/rotate/translate into the texture coordinate.
    /// </summary>
    private void ComputeMesh(double time)
    {
        var p = _preset;
        var warpAnimSpeed = p?.GetBase("fwarpanimspeed", 1.0) ?? 1.0;
        var warpScale = p?.GetBase("fwarpscale", 1.0) ?? 1.0;
        if (Math.Abs(warpScale) < 1e-6) warpScale = 1.0;

        var warpTime = time * warpAnimSpeed;
        var warpScaleInv = 1.0 / warpScale;
        var f0 = 11.68 + 4.0 * Math.Cos(warpTime * 1.413 + 10.0);
        var f1 = 8.77 + 3.0 * Math.Cos(warpTime * 1.113 + 7.0);
        var f2 = 10.54 + 3.0 * Math.Cos(warpTime * 1.233 + 3.0);
        var f3 = 11.49 + 4.0 * Math.Cos(warpTime * 0.933 + 5.0);

        var ax = _vars.Get(_slot.AspectX);
        var ay = _vars.Get(_slot.AspectY);
        var invAx = 1.0 / ax;
        var invAy = 1.0 / ay;

        // This frame's per-frame outputs; each vertex starts the per-pixel pass from these.
        var pfZoom = _vars.Get(_slot.Zoom);
        var pfZoomExp = _vars.Get(_slot.ZoomExp);
        var pfRot = _vars.Get(_slot.Rot);
        var pfWarp = _vars.Get(_slot.Warp);
        var pfCx = _vars.Get(_slot.Cx);
        var pfCy = _vars.Get(_slot.Cy);
        var pfDx = _vars.Get(_slot.Dx);
        var pfDy = _vars.Get(_slot.Dy);
        var pfSx = _vars.Get(_slot.Sx);
        var pfSy = _vars.Get(_slot.Sy);

        var hasPixel = !_pixelProgram.IsEmpty;
        var mesh = _mesh;
        var cols = mesh.Cols;
        var rows = mesh.Rows;
        var verts = mesh.Vertices;

        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < cols; i++)
            {
                var n = (j * cols + i) * 4;
                double xc = verts[n];       // clip-space x, -1..1
                double yc = verts[n + 1];   // clip-space y, -1..1 (y up)

                var rad = Math.Sqrt(xc * xc * ax * ax + yc * yc * ay * ay);
                var ang = xc == 0.0 && yc == 0.0 ? 0.0 : Math.Atan2(yc * ay, xc * ax);

                double zoom = pfZoom, zoomExp = pfZoomExp, rot = pfRot, warp = pfWarp;
                double cx = pfCx, cy = pfCy, dx = pfDx, dy = pfDy, sx = pfSx, sy = pfSy;

                if (hasPixel)
                {
                    _vars.Set(_slot.PpX, xc * 0.5 * ax + 0.5);
                    _vars.Set(_slot.PpY, -yc * 0.5 * ay + 0.5); // MilkDrop convention: 0 top, 1 bottom
                    _vars.Set(_slot.PpRad, rad);
                    _vars.Set(_slot.PpAng, ang);
                    _vars.Set(_slot.Zoom, pfZoom);
                    _vars.Set(_slot.ZoomExp, pfZoomExp);
                    _vars.Set(_slot.Rot, pfRot);
                    _vars.Set(_slot.Warp, pfWarp);
                    _vars.Set(_slot.Cx, pfCx);
                    _vars.Set(_slot.Cy, pfCy);
                    _vars.Set(_slot.Dx, pfDx);
                    _vars.Set(_slot.Dy, pfDy);
                    _vars.Set(_slot.Sx, pfSx);
                    _vars.Set(_slot.Sy, pfSy);

                    _pixelProgram.Execute(_ctx);

                    zoom = _vars.Get(_slot.Zoom);
                    zoomExp = _vars.Get(_slot.ZoomExp);
                    rot = _vars.Get(_slot.Rot);
                    warp = _vars.Get(_slot.Warp);
                    cx = _vars.Get(_slot.Cx);
                    cy = _vars.Get(_slot.Cy);
                    dx = _vars.Get(_slot.Dx);
                    dy = _vars.Get(_slot.Dy);
                    sx = _vars.Get(_slot.Sx);
                    sy = _vars.Get(_slot.Sy);
                }

                if (Math.Abs(zoom) < 1e-4) zoom = 1e-4;
                if (zoomExp <= 0.0) zoomExp = 1.0;
                if (Math.Abs(sx) < 1e-4) sx = 1e-4;
                if (Math.Abs(sy) < 1e-4) sy = 1e-4;

                var zoom2 = Math.Pow(zoom, Math.Pow(zoomExp, rad * 2.0 - 1.0));
                var zoom2Inv = 1.0 / zoom2;

                var u = xc * ax * 0.5 * zoom2Inv + 0.5;
                var v = -yc * ay * 0.5 * zoom2Inv + 0.5;

                // stretch about the centre
                u = (u - cx) / sx + cx;
                v = (v - cy) / sy + cy;

                // warp ripple
                u += warp * 0.0035 * Math.Sin(warpTime * 0.333 + warpScaleInv * (xc * f0 - yc * f3));
                v += warp * 0.0035 * Math.Cos(warpTime * 0.375 - warpScaleInv * (xc * f2 + yc * f1));
                u += warp * 0.0035 * Math.Cos(warpTime * 0.753 - warpScaleInv * (xc * f1 - yc * f2));
                v += warp * 0.0035 * Math.Sin(warpTime * 0.825 + warpScaleInv * (xc * f0 + yc * f3));

                // rotate about the centre
                var u2 = u - cx;
                var v2 = v - cy;
                var cr = Math.Cos(rot);
                var sr = Math.Sin(rot);
                u = u2 * cr - v2 * sr + cx;
                v = u2 * sr + v2 * cr + cy;

                // translate
                u -= dx;
                v -= dy;

                // undo the aspect fix applied above
                u = (u - 0.5) * invAx + 0.5;
                v = (v - 0.5) * invAy + 0.5;

                // store; flip v from MilkDrop's top-left origin to our bottom-up GL texture
                verts[n + 2] = (float)u;
                verts[n + 3] = (float)(1.0 - v);
            }
        }
    }

    public void ResetAudio() => _audio.Reset();

    private void WriteBuiltIns(double time, int frame, double fps)
    {
        _vars.Set(_slot.Time, time);
        _vars.Set(_slot.Frame, frame);
        _vars.Set(_slot.Fps, fps);
        _vars.Set(_slot.MeshX, MeshX);
        _vars.Set(_slot.MeshY, MeshY);
        _vars.Set(_slot.AspectX, 1.0);
        _vars.Set(_slot.AspectY, 1.0);
    }

    // Per-frame output variables are reset to their preset base values each frame; the per-frame
    // equations then read and adjust them (MilkDrop semantics).
    private void SeedPerFrameDefaults(MilkdropPreset p)
    {
        _vars.Set(_slot.Decay, p.GetBase("fdecay", 0.98));
        _vars.Set(_slot.Gamma, p.GetBase("fgammaadj", 2.0));
        _vars.Set(_slot.Zoom, p.GetBase("zoom", 1.0));
        _vars.Set(_slot.ZoomExp, p.GetBase("zoomexp", 1.0));
        _vars.Set(_slot.Rot, p.GetBase("rot", 0.0));
        _vars.Set(_slot.Warp, p.GetBase("warp", 1.0));
        _vars.Set(_slot.Cx, p.GetBase("cx", 0.5));
        _vars.Set(_slot.Cy, p.GetBase("cy", 0.5));
        _vars.Set(_slot.Dx, p.GetBase("dx", 0.0));
        _vars.Set(_slot.Dy, p.GetBase("dy", 0.0));
        _vars.Set(_slot.Sx, p.GetBase("sx", 1.0));
        _vars.Set(_slot.Sy, p.GetBase("sy", 1.0));
        _vars.Set(_slot.WaveR, p.GetBase("wave_r", 1.0));
        _vars.Set(_slot.WaveG, p.GetBase("wave_g", 1.0));
        _vars.Set(_slot.WaveB, p.GetBase("wave_b", 1.0));
        _vars.Set(_slot.WaveA, p.GetBase("wave_a", 1.0));
        _vars.Set(_slot.WaveX, p.GetBase("wave_x", 0.5));
        _vars.Set(_slot.WaveY, p.GetBase("wave_y", 0.5));
        _vars.Set(_slot.WaveMystery, p.GetBase("wave_mystery", p.GetBase("fwavparam", 0.0)));
        _vars.Set(_slot.EchoZoom, p.GetBase("fvideoechozoom", 1.0));
        _vars.Set(_slot.EchoAlpha, p.GetBase("fvideoechoalpha", 0.0));
        _vars.Set(_slot.EchoOrient, p.GetBase("nvideoechoorientation", 0.0));
        _vars.Set(_slot.WaveMode, p.GetBase("nwavemode", 0.0));
    }

    private FrameState ReadFrameState() => new()
    {
        Decay = _vars.Get(_slot.Decay),
        Gamma = _vars.Get(_slot.Gamma),
        Zoom = _vars.Get(_slot.Zoom),
        Rot = _vars.Get(_slot.Rot),
        Warp = _vars.Get(_slot.Warp),
        Cx = _vars.Get(_slot.Cx),
        Cy = _vars.Get(_slot.Cy),
        Dx = _vars.Get(_slot.Dx),
        Dy = _vars.Get(_slot.Dy),
        Sx = _vars.Get(_slot.Sx),
        Sy = _vars.Get(_slot.Sy),
        WaveR = _vars.Get(_slot.WaveR),
        WaveG = _vars.Get(_slot.WaveG),
        WaveB = _vars.Get(_slot.WaveB),
        WaveA = _vars.Get(_slot.WaveA),
        WaveX = _vars.Get(_slot.WaveX),
        WaveY = _vars.Get(_slot.WaveY),
        WaveMystery = _vars.Get(_slot.WaveMystery),
        WaveMode = (int)_vars.Get(_slot.WaveMode),
        EchoZoom = _vars.Get(_slot.EchoZoom),
        EchoAlpha = _vars.Get(_slot.EchoAlpha),
        EchoOrient = (int)_vars.Get(_slot.EchoOrient),
    };

    /// <summary>Cached slot indices of the built-in variables, interned once per preset load.</summary>
    private readonly struct Slots
    {
        public readonly int Time, Frame, Fps, MeshX, MeshY, AspectX, AspectY;
        public readonly int Bass, Mid, Treb, BassAtt, MidAtt, TrebAtt;
        public readonly int Decay, Gamma, Zoom, ZoomExp, Rot, Warp, Cx, Cy, Dx, Dy, Sx, Sy;
        public readonly int PpX, PpY, PpRad, PpAng;
        public readonly int WaveR, WaveG, WaveB, WaveA, WaveX, WaveY, WaveMystery, WaveMode;
        public readonly int EchoZoom, EchoAlpha, EchoOrient;
        public readonly int[] Q;

        private Slots(VariableTable v)
        {
            Q = new int[32];
            for (var i = 0; i < 32; i++) Q[i] = v.Intern("q" + (i + 1));

            Time = v.Intern("time"); Frame = v.Intern("frame"); Fps = v.Intern("fps");
            MeshX = v.Intern("meshx"); MeshY = v.Intern("meshy");
            AspectX = v.Intern("aspectx"); AspectY = v.Intern("aspecty");
            Bass = v.Intern("bass"); Mid = v.Intern("mid"); Treb = v.Intern("treb");
            BassAtt = v.Intern("bass_att"); MidAtt = v.Intern("mid_att"); TrebAtt = v.Intern("treb_att");
            Decay = v.Intern("decay"); Gamma = v.Intern("gamma");
            Zoom = v.Intern("zoom"); ZoomExp = v.Intern("zoomexp");
            Rot = v.Intern("rot"); Warp = v.Intern("warp");
            Cx = v.Intern("cx"); Cy = v.Intern("cy"); Dx = v.Intern("dx"); Dy = v.Intern("dy");
            Sx = v.Intern("sx"); Sy = v.Intern("sy");
            PpX = v.Intern("x"); PpY = v.Intern("y"); PpRad = v.Intern("rad"); PpAng = v.Intern("ang");
            WaveR = v.Intern("wave_r"); WaveG = v.Intern("wave_g"); WaveB = v.Intern("wave_b");
            WaveA = v.Intern("wave_a"); WaveX = v.Intern("wave_x"); WaveY = v.Intern("wave_y");
            WaveMystery = v.Intern("wave_mystery"); WaveMode = v.Intern("wave_mode");
            EchoZoom = v.Intern("echo_zoom"); EchoAlpha = v.Intern("echo_alpha");
            EchoOrient = v.Intern("echo_orient");
        }

        public static Slots Intern(VariableTable v) => new(v);
    }
}
