using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Visualizations.Abstractions;
using PlattaPlayer.Visualizations.MilkDrop;
using PlattaPlayer.Visualizations.MilkDrop.Presets;
using PlattaPlayer.Visualizations.MilkDrop.Shaders;

namespace PlattaPlayer.Visualizations.MilkDrop.Plugin;

/// <summary>
/// The MilkDrop visualizer surface: an Avalonia <see cref="OpenGlControlBase"/> that drives a
/// <see cref="MilkdropEngine"/> each frame, feeds it the live PCM from an <see cref="IAudioTap"/>, and
/// renders the classic warp-feedback look — every frame warps the previous frame (per-pixel mesh warp
/// from the preset's equations), fades it by the decay, and draws the waveform on top.
///
/// Classic presets use the built-in mesh-warp + blit passes. MilkDrop2 presets that carry HLSL warp/comp
/// shaders get those translated to GLSL (see <see cref="ShaderTranslator"/>) and run in place of the
/// built-in warp/present passes; auxiliary blur/noise samplers fall back to the source frame for now.
///
/// Presets are cycled from <see cref="PresetsPath"/>; switching crossfades between two independently
/// rendered <see cref="MilkLayer"/>s over <see cref="BlendDuration"/> so transitions are smooth.
/// </summary>
public sealed class MilkdropVisualizer : OpenGlControlBase, IVisualizationController, IVisualizerHealth
{
    public static readonly StyledProperty<IAudioTap?> TapProperty =
        AvaloniaProperty.Register<MilkdropVisualizer, IAudioTap?>(nameof(Tap));

    public IAudioTap? Tap
    {
        get => GetValue(TapProperty);
        set => SetValue(TapProperty, value);
    }

    /// <summary>
    /// Folder of <c>.milk</c> presets to cycle through. When null/empty/missing, the built-in classic
    /// preset is used and cycling is disabled.
    /// </summary>
    public static readonly StyledProperty<string?> PresetsPathProperty =
        AvaloniaProperty.Register<MilkdropVisualizer, string?>(nameof(PresetsPath));

    public string? PresetsPath
    {
        get => GetValue(PresetsPathProperty);
        set => SetValue(PresetsPathProperty, value);
    }

    /// <summary>
    /// Private folder this visualizer may write to (supplied by the host). Used for the GL error log so a
    /// headless/first-run GPU failure is diagnosable. When null, the error log is skipped.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>Seconds each preset is shown before auto-advancing. Zero or less disables auto-cycling.</summary>
    public static readonly StyledProperty<double> PresetDurationProperty =
        AvaloniaProperty.Register<MilkdropVisualizer, double>(nameof(PresetDuration), 20.0);

    public double PresetDuration
    {
        get => GetValue(PresetDurationProperty);
        set => SetValue(PresetDurationProperty, value);
    }

    /// <summary>Seconds a preset switch crossfades over. Zero or less switches instantly (no blend).</summary>
    public static readonly StyledProperty<double> BlendDurationProperty =
        AvaloniaProperty.Register<MilkdropVisualizer, double>(nameof(BlendDuration), 2.5);

    public double BlendDuration
    {
        get => GetValue(BlendDurationProperty);
        set => SetValue(BlendDurationProperty, value);
    }

    private readonly float[] _samples = new float[1024];
    private readonly float[] _waveVerts = new float[1024 * 2];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private GlBindings? _gl;
    private bool _isGles;
    private bool _failed; // set if GL init/render threw; stops further attempts and logs the cause once.

    // Shared GL programs (preset-independent).
    private int _meshProgram;
    private int _blitProgram;
    private int _waveProgram;
    private int _colorProgram;
    private int _fadeProgram;
    private int _quadVbo;
    private int _meshVbo;
    private int _meshIbo;
    private int _waveVbo;
    private int _geomVbo;
    private int _meshIndexCount;

    // Disk preset cycling + crossfade.
    private PresetLibrary? _library;
    private double _nextSwitchTime;
    private double _blendStart;
    private MilkLayer _current = new();
    private MilkLayer _spare = new();
    private MilkLayer? _incoming; // == _spare while a crossfade is in progress

    // mesh (warp) program locations.
    private int _mPos, _mUv, _mTex, _mDecay;
    // blit (present) program locations.
    private int _bPos, _bUv, _bTex;
    // wave program locations.
    private int _wPos, _uColor;
    // colored-geometry (custom waves/shapes) program locations.
    private int _cPos, _cColor, _cPointSize;
    // fade present program locations.
    private int _fPos, _fUv, _fTex, _fAlpha;

    // Frosted-glass "blur behind" support: a separable gaussian blur of the presented frame, painted into
    // the screen rectangles of registered chrome controls so they look like frosted panels over the viz.
    private int _blurProgram;
    private int _blPos, _blUv, _blTex, _blDir;
    private int _regionVbo;
    private int _blurTexA, _blurFboA, _blurTexB, _blurFboB, _blurW, _blurH;
    private readonly float[] _regionVerts = new float[16]; // 4 verts × (x,y,u,v)

    /// <summary>
    /// Controls whose on-screen rectangles should show a blurred copy of the visualization behind them
    /// (frosted-glass effect). Populated by the host (e.g. the window code-behind). Each must give itself a
    /// transparent/lightly-tinted background so the blurred visualization shows through.
    /// </summary>
    public List<Visual> BlurTargets { get; } = new();

    // Manual preset requests from the UI thread, applied on the render thread (GL context current) next
    // frame. _pendingStep: +1/-1 for next/previous; _pendingRandom: jump to a random preset.
    private volatile int _pendingStep;
    private volatile bool _pendingRandom;

    /// <summary>Raised (on the render thread) whenever the visible preset changes; carries its name.</summary>
    public event Action<string>? PresetChanged;

    /// <inheritdoc />
    public void NextPreset() { _pendingStep = 1; RequestNextFrameRendering(); }

    /// <inheritdoc />
    public void PreviousPreset() { _pendingStep = -1; RequestNextFrameRendering(); }

    /// <inheritdoc />
    public void RandomPreset() { _pendingRandom = true; RequestNextFrameRendering(); }

    private double _lastTime;
    private long _heartbeat;

    /// <inheritdoc />
    public long Heartbeat => Interlocked.Read(ref _heartbeat);

    /// <inheritdoc />
    public void Resume() => RequestNextFrameRendering();

    private static readonly float[] QuadData =
    {
        //  x,    y,   u,   v
        -1f, -1f, 0f, 0f,
         1f, -1f, 1f, 0f,
        -1f,  1f, 0f, 1f,
         1f,  1f, 1f, 1f,
    };

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try { InitGl(gl); }
        catch (Exception ex) { _failed = true; LogFailure("init", ex); }
    }

    // Writes a GL init/render failure to the host-supplied data folder's milkdrop-error.log (and Debug) so
    // a headless/first-run GPU failure is diagnosable. Diagnostics must never throw.
    private void LogFailure(string phase, Exception ex)
    {
        Debug.WriteLine($"MilkDrop GL {phase} failed: {ex}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            var path = Path.Combine(DataDirectory, "milkdrop-error.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] MilkDrop GL {phase} failed:\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }

    private void InitGl(GlInterface gl)
    {
        _gl = new GlBindings(gl);
        _isGles = GlVersion.Type == GlProfileType.OpenGLES;

        _meshProgram = _gl.BuildProgram(Vertex(quad: true), FragmentMesh());
        _blitProgram = _gl.BuildProgram(Vertex(quad: true), FragmentBlit());
        _waveProgram = _gl.BuildProgram(Vertex(quad: false), FragmentSolid());

        _mPos = _gl.GetAttrib(_meshProgram, "aPos");
        _mUv = _gl.GetAttrib(_meshProgram, "aUv");
        _mTex = _gl.GetUniform(_meshProgram, "uTex");
        _mDecay = _gl.GetUniform(_meshProgram, "uDecay");

        _bPos = _gl.GetAttrib(_blitProgram, "aPos");
        _bUv = _gl.GetAttrib(_blitProgram, "aUv");
        _bTex = _gl.GetUniform(_blitProgram, "uTex");

        _wPos = _gl.GetAttrib(_waveProgram, "aPos");
        _uColor = _gl.GetUniform(_waveProgram, "uColor");

        _colorProgram = _gl.BuildProgram(VertexColor(), FragmentColor());
        _cPos = _gl.GetAttrib(_colorProgram, "aPos");
        _cColor = _gl.GetAttrib(_colorProgram, "aColor");
        _cPointSize = _gl.GetUniform(_colorProgram, "uPointSize");
        _geomVbo = _gl.GenBuffer();

        _fadeProgram = _gl.BuildProgram(Vertex(quad: true), FragmentFade());
        _fPos = _gl.GetAttrib(_fadeProgram, "aPos");
        _fUv = _gl.GetAttrib(_fadeProgram, "aUv");
        _fTex = _gl.GetUniform(_fadeProgram, "uTex");
        _fAlpha = _gl.GetUniform(_fadeProgram, "uAlpha");

        _blurProgram = _gl.BuildProgram(Vertex(quad: true), FragmentBlur());
        _blPos = _gl.GetAttrib(_blurProgram, "aPos");
        _blUv = _gl.GetAttrib(_blurProgram, "aUv");
        _blTex = _gl.GetUniform(_blurProgram, "uTex");
        _blDir = _gl.GetUniform(_blurProgram, "uDir");
        _regionVbo = _gl.GenBuffer();

        _quadVbo = _gl.GenBuffer();
        _gl.BindBuffer(GlBindings.ArrayBuffer, _quadVbo);
        _gl.BufferData(GlBindings.ArrayBuffer, QuadData, GlBindings.StaticDraw);
        _waveVbo = _gl.GenBuffer();

        _library = PresetLibrary.FromDirectory(PresetsPath);
        var preset = _library.Advance(1) ?? MilkParser.Parse(DefaultPresets.Source, DefaultPresets.Name);
        LoadPresetInto(_current, preset);
        PresetChanged?.Invoke(preset.Name);

        // The warp grid's topology is fixed for a given mesh size; upload its index buffer once. (Both
        // layers share the same mesh resolution, so the index buffer is built here and reused.)
        var mesh = _current.Engine.Mesh;
        _meshIndexCount = mesh.IndexCount;
        _meshVbo = _gl.GenBuffer();
        _meshIbo = _gl.GenBuffer();
        _gl.BindBuffer(GlBindings.ElementArrayBuffer, _meshIbo);
        _gl.BufferData(GlBindings.ElementArrayBuffer, mesh.Indices, GlBindings.StaticDraw);

        ScheduleNextSwitch();
    }

    // Loads a preset into a layer and (re)builds its translated warp/comp shaders, disposing the layer's
    // previous shader programs first.
    private void LoadPresetInto(MilkLayer layer, MilkdropPreset preset)
    {
        layer.Engine.Load(preset, _clock.Elapsed.TotalSeconds);
        layer.ShaderFrame = 0;
        DisposeLayerShaders(layer);
        if (_gl is not null && preset.HasShaders)
        {
            layer.WarpShader = TryBuildShader(preset.WarpShader, isComposite: false);
            layer.CompShader = TryBuildShader(preset.CompShader, isComposite: true);
        }
    }

    private void DisposeLayerShaders(MilkLayer layer)
    {
        if (_gl is not null)
        {
            if (layer.WarpShader is not null) _gl.DeleteProgram(layer.WarpShader.Program);
            if (layer.CompShader is not null) _gl.DeleteProgram(layer.CompShader.Program);
        }
        layer.WarpShader = layer.CompShader = null;
    }

    private void ScheduleNextSwitch()
        => _nextSwitchTime = _clock.Elapsed.TotalSeconds + Math.Max(1.0, PresetDuration);

    // Begins a crossfade to the next disk preset once the current one's display time has elapsed. No-op
    // when there is no library, it is empty, auto-cycling is disabled, or a blend is already running.
    private void MaybeCyclePreset(double now)
    {
        if (_library is null || _library.IsEmpty || PresetDuration <= 0 || _incoming is not null || now < _nextSwitchTime)
            return;

        var next = _library.Advance(1);
        if (next is not null) BeginSwitch(next, now);
        else ScheduleNextSwitch();
    }

    // Applies a pending manual preset request (next/previous/random) on the render thread. A manual switch
    // supersedes any in-progress crossfade so the chosen preset takes effect immediately.
    private void ApplyPendingPresetChange(double now)
    {
        if (_library is null || _library.IsEmpty) { _pendingStep = 0; _pendingRandom = false; return; }

        MilkdropPreset? next;
        if (_pendingRandom) next = _library.Random();
        else if (_pendingStep != 0) next = _library.Advance(_pendingStep);
        else return;

        _pendingRandom = false;
        _pendingStep = 0;
        if (next is null) return;

        _incoming = null; // drop any partially-blended incoming layer; restart the crossfade cleanly
        BeginSwitch(next, now);
    }

    // Switches to <paramref name="next"/>, either instantly or via a crossfade, and announces the change.
    private void BeginSwitch(MilkdropPreset next, double now)
    {
        if (BlendDuration <= 0)
        {
            // Instant switch: load straight into the visible layer, no second pipeline.
            LoadPresetInto(_current, next);
        }
        else
        {
            LoadPresetInto(_spare, next);
            _incoming = _spare;
            _blendStart = now;
        }
        PresetChanged?.Invoke(next.Name);
        ScheduleNextSwitch();
    }

    // Translate the preset's HLSL warp/comp shaders to GLSL and compile them. Any failure (unsupported
    // construct, GLSL compile error) returns null so the built-in classic pass runs instead.
    private MilkShaderProgram? TryBuildShader(string hlsl, bool isComposite)
    {
        if (_gl is null || string.IsNullOrWhiteSpace(hlsl)) return null;
        try
        {
            var glsl = ShaderTranslator.Translate(hlsl, isComposite, _isGles);
            return new MilkShaderProgram(_gl, ShaderVertex(), glsl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MilkDrop {(isComposite ? "comp" : "warp")} shader translation failed: {ex.Message}");
            return null;
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is null) return;
        if (_quadVbo != 0) _gl.DeleteBuffer(_quadVbo);
        if (_meshVbo != 0) _gl.DeleteBuffer(_meshVbo);
        if (_meshIbo != 0) _gl.DeleteBuffer(_meshIbo);
        if (_waveVbo != 0) _gl.DeleteBuffer(_waveVbo);
        if (_geomVbo != 0) _gl.DeleteBuffer(_geomVbo);
        if (_meshProgram != 0) _gl.DeleteProgram(_meshProgram);
        if (_blitProgram != 0) _gl.DeleteProgram(_blitProgram);
        if (_waveProgram != 0) _gl.DeleteProgram(_waveProgram);
        if (_colorProgram != 0) _gl.DeleteProgram(_colorProgram);
        if (_fadeProgram != 0) _gl.DeleteProgram(_fadeProgram);
        if (_blurProgram != 0) _gl.DeleteProgram(_blurProgram);
        if (_regionVbo != 0) _gl.DeleteBuffer(_regionVbo);
        DeleteBlurTargets();
        DisposeLayerShaders(_current);
        DisposeLayerShaders(_spare);
        DeleteLayerTargets(_current);
        DeleteLayerTargets(_spare);
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_failed) return;
        try { RenderGl(gl, fb); }
        catch (Exception ex) { _failed = true; LogFailure("render", ex); }
    }

    private void RenderGl(GlInterface gl, int fb)
    {
        var g = _gl;
        if (g is null) return;

        // Avalonia doesn't set the GL viewport before calling us, so derive the surface pixel size from the
        // control's layout bounds and the top-level render scaling (matches the size of the framebuffer
        // Avalonia hands us in <paramref name="fb"/>).
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var h = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        var count = Tap?.ReadWaveform(_samples) ?? 0;

        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastTime;
        _lastTime = now;
        var fps = dt > 1e-4 ? Math.Clamp(1.0 / dt, 1.0, 240.0) : 60.0;

        ApplyPendingPresetChange(now);
        MaybeCyclePreset(now);

        // Render each active preset's scene into its own output texture.
        EnsureLayerTargets(_current, w, h);
        RenderLayerScene(g, _current, w, h, count, now, fps);
        if (_incoming is not null)
        {
            EnsureLayerTargets(_incoming, w, h);
            RenderLayerScene(g, _incoming, w, h, count, now, fps);
        }

        // Present to the screen: the current layer opaque, the incoming layer faded in over it.
        g.BindFramebuffer(fb);
        g.Viewport(0, 0, w, h);
        PresentLayer(g, _current.OutTex, 1f, blend: false);

        if (_incoming is not null)
        {
            var t = (float)Math.Clamp((now - _blendStart) / Math.Max(1e-3, BlendDuration), 0.0, 1.0);
            PresentLayer(g, _incoming.OutTex, t, blend: true);

            if (t >= 1f)
            {
                // Crossfade complete: promote the incoming layer; the old one becomes the spare to reuse.
                (_current, _spare) = (_incoming, _current);
                _incoming = null;
            }
        }

        DrawFrostedRegions(g, fb, w, h, scaling);

        Interlocked.Increment(ref _heartbeat);
        RequestNextFrameRendering();
    }

    // Frosted-glass pass: build a blurred copy of the presented frame and paint it into the on-screen
    // rectangles of the registered chrome controls so they read as frosted panels floating over the viz.
    private void DrawFrostedRegions(GlBindings g, int fb, int w, int h, double scaling)
    {
        if (BlurTargets.Count == 0) return;

        // Build a quarter-res blurred copy of the current frame: downsample, then blur H then V.
        EnsureBlurTargets(w, h);
        g.BindFramebuffer(_blurFboA);
        g.Viewport(0, 0, _blurW, _blurH);
        Blit(g, _current.OutTex);
        BlurPass(g, _blurTexA, _blurFboB, 1f / _blurW, 0f);
        BlurPass(g, _blurTexB, _blurFboA, 0f, 1f / _blurH);

        // Paint the blurred texture into each control's screen rectangle (sampled at the matching sub-rect).
        g.BindFramebuffer(fb);
        g.Viewport(0, 0, w, h);
        g.Disable(GlBindings.Blend);
        g.UseProgram(_blitProgram);
        g.ActiveTexture(GlBindings.Texture0);
        g.BindTexture(GlBindings.Texture2D, _blurTexA);
        g.Uniform1i(_bTex, 0);

        foreach (var target in BlurTargets)
        {
            if (!target.IsVisible) continue;
            if (target.TranslatePoint(new Point(0, 0), this) is not { } o) continue;
            var b = target.Bounds;
            if (b.Width <= 0 || b.Height <= 0) continue;

            var px = (float)(o.X * scaling);
            var py = (float)(o.Y * scaling);
            var rw = (float)(b.Width * scaling);
            var rh = (float)(b.Height * scaling);

            var ndcL = px / w * 2f - 1f;
            var ndcR = (px + rw) / w * 2f - 1f;
            var ndcTop = 1f - 2f * py / h;
            var ndcBottom = 1f - 2f * (py + rh) / h;
            var uL = px / w;
            var uR = (px + rw) / w;
            var vTop = 1f - py / h;
            var vBottom = 1f - (py + rh) / h;

            // BL, BR, TL, TR to match the TriangleStrip winding used by QuadData.
            _regionVerts[0] = ndcL; _regionVerts[1] = ndcBottom; _regionVerts[2] = uL; _regionVerts[3] = vBottom;
            _regionVerts[4] = ndcR; _regionVerts[5] = ndcBottom; _regionVerts[6] = uR; _regionVerts[7] = vBottom;
            _regionVerts[8] = ndcL; _regionVerts[9] = ndcTop; _regionVerts[10] = uL; _regionVerts[11] = vTop;
            _regionVerts[12] = ndcR; _regionVerts[13] = ndcTop; _regionVerts[14] = uR; _regionVerts[15] = vTop;

            g.BindBuffer(GlBindings.ArrayBuffer, _regionVbo);
            g.BufferData(GlBindings.ArrayBuffer, _regionVerts, GlBindings.DynamicDraw);
            g.EnableVertexAttrib(_bPos);
            g.VertexAttribFloat(_bPos, 2, 4, 0);
            g.EnableVertexAttrib(_bUv);
            g.VertexAttribFloat(_bUv, 2, 4, 2);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
        }
    }

    // One direction of a separable gaussian blur: sample <paramref name="srcTex"/> into <paramref name="dstFbo"/>
    // offset along <paramref name="dirX"/>/<paramref name="dirY"/> (one of them zero per pass).
    private void BlurPass(GlBindings g, int srcTex, int dstFbo, float dirX, float dirY)
    {
        g.BindFramebuffer(dstFbo);
        g.Viewport(0, 0, _blurW, _blurH);
        g.Disable(GlBindings.Blend);
        g.UseProgram(_blurProgram);
        g.BindBuffer(GlBindings.ArrayBuffer, _quadVbo);
        g.EnableVertexAttrib(_blPos);
        g.VertexAttribFloat(_blPos, 2, 4, 0);
        g.EnableVertexAttrib(_blUv);
        g.VertexAttribFloat(_blUv, 2, 4, 2);
        g.ActiveTexture(GlBindings.Texture0);
        g.BindTexture(GlBindings.Texture2D, srcTex);
        g.Uniform1i(_blTex, 0);
        g.Uniform2f(_blDir, dirX, dirY);
        g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
    }

    // (Re)creates the quarter-res ping-pong textures the blur passes render into.
    private void EnsureBlurTargets(int w, int h)
    {
        if (_gl is null) return;
        var bw = Math.Max(1, w / 4);
        var bh = Math.Max(1, h / 4);
        if (bw == _blurW && bh == _blurH && _blurTexA != 0) return;
        DeleteBlurTargets();
        (_blurTexA, _blurFboA) = CreateTarget(_gl, bw, bh);
        (_blurTexB, _blurFboB) = CreateTarget(_gl, bw, bh);
        _blurW = bw;
        _blurH = bh;
    }

    private void DeleteBlurTargets()
    {
        if (_gl is null) return;
        if (_blurTexA != 0) _gl.DeleteTexture(_blurTexA);
        if (_blurTexB != 0) _gl.DeleteTexture(_blurTexB);
        if (_blurFboA != 0) _gl.DeleteFramebuffer(_blurFboA);
        if (_blurFboB != 0) _gl.DeleteFramebuffer(_blurFboB);
        _blurTexA = _blurTexB = _blurFboA = _blurFboB = _blurW = _blurH = 0;
    }

    // Renders one layer: warp the previous frame, overlay its custom shapes/waves and the waveform, then
    // composite into the layer's output texture. Leaves the result in <paramref name="layer"/>.OutTex.
    private void RenderLayerScene(GlBindings g, MilkLayer layer, int w, int h, int count, double now, double fps)
    {
        var state = layer.Engine.Update(_samples, count, now, fps);
        var inputs = BuildShaderInputs(layer, now, (float)fps, w, h, state);

        // Pass 1: warp the previous frame (TexA) into TexB.
        g.BindFramebuffer(layer.FboB);
        g.Viewport(0, 0, w, h);
        if (layer.WarpShader is not null) DrawWarpMeshShader(g, layer, layer.WarpShader, layer.TexA, inputs);
        else DrawWarpMesh(g, layer, layer.TexA, state);

        // Pass 2: custom shapes and waves, then the built-in waveform.
        DrawCustomShapes(g, layer);
        DrawCustomWaves(g, layer);
        DrawWaveform(g, count, state);

        // Pass 3: composite TexB into the layer's output (comp shader if present, else a plain blit).
        g.BindFramebuffer(layer.OutFbo);
        g.Viewport(0, 0, w, h);
        if (layer.CompShader is not null) DrawCompShader(g, layer, layer.CompShader, layer.TexB, inputs);
        else Blit(g, layer.TexB);

        (layer.TexA, layer.TexB) = (layer.TexB, layer.TexA);
        (layer.FboA, layer.FboB) = (layer.FboB, layer.FboA);
    }

    private void DrawWarpMesh(GlBindings g, MilkLayer layer, int sourceTexture, FrameState s)
    {
        var mesh = layer.Engine.Mesh;

        g.Disable(GlBindings.Blend);
        g.UseProgram(_meshProgram);

        // Vertex positions are static; only the UVs change, so re-upload the interleaved vertex array.
        g.BindBuffer(GlBindings.ArrayBuffer, _meshVbo);
        g.BufferData(GlBindings.ArrayBuffer, mesh.Vertices, GlBindings.DynamicDraw);
        g.BindBuffer(GlBindings.ElementArrayBuffer, _meshIbo);

        g.EnableVertexAttrib(_mPos);
        g.VertexAttribFloat(_mPos, 2, 4, 0);
        g.EnableVertexAttrib(_mUv);
        g.VertexAttribFloat(_mUv, 2, 4, 2);

        g.ActiveTexture(GlBindings.Texture0);
        g.BindTexture(GlBindings.Texture2D, sourceTexture);
        g.Uniform1i(_mTex, 0);
        g.Uniform1f(_mDecay, (float)Math.Clamp(s.Decay, 0.0, 1.0));

        g.DrawElementsU16(GlBindings.Triangles, _meshIndexCount);
    }

    private MilkShaderInputs BuildShaderInputs(MilkLayer layer, double now, float fps, int w, int h, FrameState s)
    {
        var a = layer.Engine.Audio;
        // Square viewport → aspect 1; otherwise the longer axis is >1, matching MilkDrop's convention.
        var ax = w >= h ? (float)w / h : 1f;
        var ay = h > w ? (float)h / w : 1f;
        return new MilkShaderInputs
        {
            MainTexture = 0, // set per-pass to the bound source texture
            Time = (float)now,
            Fps = fps,
            Frame = layer.ShaderFrame++,
            Bass = a.Bass,
            BassAtt = a.BassAtt,
            Mid = a.Mid,
            MidAtt = a.MidAtt,
            Treb = a.Treb,
            TrebAtt = a.TrebAtt,
            Vol = (a.Bass + a.Mid + a.Treb) / 3f,
            Decay = (float)Math.Clamp(s.Decay, 0.0, 1.0),
            Width = w,
            Height = h,
            AspectX = ax,
            AspectY = ay,
            Q = layer.Engine.QValues,
        };
    }

    // MilkDrop2 warp pass: run the translated warp shader over the per-pixel mesh. The mesh carries the
    // engine-warped UV (→ uv) and the original grid position (→ uv_orig via the vertex shader).
    private void DrawWarpMeshShader(GlBindings g, MilkLayer layer, MilkShaderProgram prog, int sourceTexture, in MilkShaderInputs s)
    {
        var mesh = layer.Engine.Mesh;

        g.Disable(GlBindings.Blend);

        g.BindBuffer(GlBindings.ArrayBuffer, _meshVbo);
        g.BufferData(GlBindings.ArrayBuffer, mesh.Vertices, GlBindings.DynamicDraw);
        g.BindBuffer(GlBindings.ElementArrayBuffer, _meshIbo);

        prog.Apply(s with { MainTexture = sourceTexture });

        g.EnableVertexAttrib(prog.APos);
        g.VertexAttribFloat(prog.APos, 2, 4, 0);
        g.EnableVertexAttrib(prog.AUv);
        g.VertexAttribFloat(prog.AUv, 2, 4, 2);

        g.DrawElementsU16(GlBindings.Triangles, _meshIndexCount);
    }

    // MilkDrop2 composite pass: run the translated comp shader over a fullscreen quad reading the frame.
    private void DrawCompShader(GlBindings g, MilkLayer layer, MilkShaderProgram prog, int sourceTexture, in MilkShaderInputs s)
    {
        g.Disable(GlBindings.Blend);

        g.BindBuffer(GlBindings.ArrayBuffer, _quadVbo);

        prog.Apply(s with { MainTexture = sourceTexture });

        g.EnableVertexAttrib(prog.APos);
        g.VertexAttribFloat(prog.APos, 2, 4, 0);
        g.EnableVertexAttrib(prog.AUv);
        g.VertexAttribFloat(prog.AUv, 2, 4, 2);

        g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
    }

    private void Blit(GlBindings g, int sourceTexture)
    {
        g.Disable(GlBindings.Blend);
        g.UseProgram(_blitProgram);

        g.BindBuffer(GlBindings.ArrayBuffer, _quadVbo);
        g.EnableVertexAttrib(_bPos);
        g.VertexAttribFloat(_bPos, 2, 4, 0);
        g.EnableVertexAttrib(_bUv);
        g.VertexAttribFloat(_bUv, 2, 4, 2);

        g.ActiveTexture(GlBindings.Texture0);
        g.BindTexture(GlBindings.Texture2D, sourceTexture);
        g.Uniform1i(_bTex, 0);

        g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
    }

    // Presents a layer's output texture to the bound framebuffer, multiplying alpha by <paramref name="alpha"/>
    // (used to fade the incoming preset in over the current one during a crossfade).
    private void PresentLayer(GlBindings g, int sourceTexture, float alpha, bool blend)
    {
        if (blend)
        {
            g.Enable(GlBindings.Blend);
            g.BlendFunc(GlBindings.SrcAlpha, GlBindings.OneMinusSrcAlpha);
        }
        else
        {
            g.Disable(GlBindings.Blend);
        }

        g.UseProgram(_fadeProgram);
        g.BindBuffer(GlBindings.ArrayBuffer, _quadVbo);
        g.EnableVertexAttrib(_fPos);
        g.VertexAttribFloat(_fPos, 2, 4, 0);
        g.EnableVertexAttrib(_fUv);
        g.VertexAttribFloat(_fUv, 2, 4, 2);

        g.ActiveTexture(GlBindings.Texture0);
        g.BindTexture(GlBindings.Texture2D, sourceTexture);
        g.Uniform1i(_fTex, 0);
        g.Uniform1f(_fAlpha, alpha);

        g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
    }

    // Draws every custom shape: the triangle-list fill, then the line-list border. Additive shapes blend
    // with GL_ONE so overlapping fills brighten, matching MilkDrop's additive mode.
    private void DrawCustomShapes(GlBindings g, MilkLayer layer)
    {
        var shapes = layer.Engine.ShapeOutputs;
        if (shapes.Count == 0) return;

        g.UseProgram(_colorProgram);
        g.Uniform1f(_cPointSize, 1f);
        g.Enable(GlBindings.Blend);

        foreach (var shape in shapes)
        {
            if (shape.FillVertexCount > 0)
            {
                SetBlend(g, shape.Additive);
                UploadColorVerts(g, shape.FillVertices, shape.FillVertexCount);
                g.DrawArrays(GlBindings.Triangles, 0, shape.FillVertexCount);
            }
            if (shape.BorderVertexCount > 0)
            {
                SetBlend(g, additive: false);
                UploadColorVerts(g, shape.BorderVertices, shape.BorderVertexCount);
                g.DrawArrays(GlBindings.Lines, 0, shape.BorderVertexCount);
            }
        }
    }

    // Draws every custom wave as a coloured line strip (or dot cloud when the preset requests dots).
    private void DrawCustomWaves(GlBindings g, MilkLayer layer)
    {
        var waves = layer.Engine.WaveOutputs;
        if (waves.Count == 0) return;

        g.UseProgram(_colorProgram);
        g.Enable(GlBindings.Blend);

        foreach (var wave in waves)
        {
            if (wave.PointCount < 2) continue;
            SetBlend(g, wave.Additive);
            g.Uniform1f(_cPointSize, wave.Thick ? 4f : 2f);
            UploadColorVerts(g, wave.Vertices, wave.PointCount);
            g.DrawArrays(wave.UseDots ? GlBindings.Points : GlBindings.LineStrip, 0, wave.PointCount);
        }
    }

    private void UploadColorVerts(GlBindings g, float[] verts, int vertexCount)
    {
        g.BindBuffer(GlBindings.ArrayBuffer, _geomVbo);
        // verts may be over-sized; upload only the floats this draw uses (6 per vertex: x,y,r,g,b,a).
        g.BufferDataRange(GlBindings.ArrayBuffer, verts, vertexCount * 6, GlBindings.DynamicDraw);
        g.EnableVertexAttrib(_cPos);
        g.VertexAttribFloat(_cPos, 2, 6, 0);
        g.EnableVertexAttrib(_cColor);
        g.VertexAttribFloat(_cColor, 4, 6, 2);
    }

    private static void SetBlend(GlBindings g, bool additive)
        => g.BlendFunc(GlBindings.SrcAlpha, additive ? GlBindings.One : GlBindings.OneMinusSrcAlpha);

    private void DrawWaveform(GlBindings g, int count, FrameState s)
    {
        if (count < 2) return;

        var n = Math.Min(count, _samples.Length);
        var yOffset = (float)((s.WaveY - 0.5) * 1.4);
        for (var i = 0; i < n; i++)
        {
            var x = -0.95f + 1.9f * i / (n - 1);
            var y = Math.Clamp(_samples[i] * 0.7f + yOffset, -1f, 1f);
            _waveVerts[i * 2] = x;
            _waveVerts[i * 2 + 1] = y;
        }

        g.Enable(GlBindings.Blend);
        g.BlendFunc(GlBindings.SrcAlpha, GlBindings.OneMinusSrcAlpha);
        g.UseProgram(_waveProgram);
        g.BindBuffer(GlBindings.ArrayBuffer, _waveVbo);
        g.BufferData(GlBindings.ArrayBuffer, _waveVerts, GlBindings.DynamicDraw);
        g.EnableVertexAttrib(_wPos);
        g.VertexAttribFloat(_wPos, 2, 2, 0);
        g.Uniform4f(_uColor, (float)s.WaveR, (float)s.WaveG, (float)s.WaveB, (float)Math.Clamp(s.WaveA, 0.0, 1.0));
        g.DrawArrays(GlBindings.LineStrip, 0, n);
    }

    private void EnsureLayerTargets(MilkLayer layer, int w, int h)
    {
        if (_gl is null || (w == layer.FbWidth && h == layer.FbHeight && layer.TexA != 0)) return;
        DeleteLayerTargets(layer);

        (layer.TexA, layer.FboA) = CreateTarget(_gl, w, h);
        (layer.TexB, layer.FboB) = CreateTarget(_gl, w, h);
        (layer.OutTex, layer.OutFbo) = CreateTarget(_gl, w, h);
        layer.FbWidth = w;
        layer.FbHeight = h;
    }

    private static (int Texture, int Fbo) CreateTarget(GlBindings g, int w, int h)
    {
        var tex = g.GenTexture();
        g.BindTexture(GlBindings.Texture2D, tex);
        g.TexImageEmpty(w, h);
        g.TexParameter(GlBindings.TextureMinFilter, GlBindings.Linear);
        g.TexParameter(GlBindings.TextureMagFilter, GlBindings.Linear);
        g.TexParameter(GlBindings.TextureWrapS, GlBindings.ClampToEdge);
        g.TexParameter(GlBindings.TextureWrapT, GlBindings.ClampToEdge);

        var fbo = g.GenFramebuffer();
        g.BindFramebuffer(fbo);
        g.AttachColorTexture(tex);
        g.ClearColor(0, 0, 0, 1);
        g.Clear(GlBindings.ColorBufferBit);
        return (tex, fbo);
    }

    private void DeleteLayerTargets(MilkLayer layer)
    {
        if (_gl is null) return;
        if (layer.TexA != 0) _gl.DeleteTexture(layer.TexA);
        if (layer.TexB != 0) _gl.DeleteTexture(layer.TexB);
        if (layer.OutTex != 0) _gl.DeleteTexture(layer.OutTex);
        if (layer.FboA != 0) _gl.DeleteFramebuffer(layer.FboA);
        if (layer.FboB != 0) _gl.DeleteFramebuffer(layer.FboB);
        if (layer.OutFbo != 0) _gl.DeleteFramebuffer(layer.OutFbo);
        layer.TexA = layer.TexB = layer.OutTex = layer.FboA = layer.FboB = layer.OutFbo = 0;
        layer.FbWidth = layer.FbHeight = 0;
    }

    private string Header() => _isGles ? "#version 100\nprecision highp float;\n" : "#version 120\n";

    private string Vertex(bool quad) => quad
        ? Header() + """
            attribute vec2 aPos;
            attribute vec2 aUv;
            varying vec2 vUv;
            void main() { vUv = aUv; gl_Position = vec4(aPos, 0.0, 1.0); }
            """
        : Header() + """
            attribute vec2 aPos;
            void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
            """;

    // Coloured-geometry program for custom waves/shapes: per-vertex RGBA, with a point size for dot waves.
    private string VertexColor() => Header() + """
        attribute vec2 aPos;
        attribute vec4 aColor;
        uniform float uPointSize;
        varying vec4 vColor;
        void main() {
            vColor = aColor;
            gl_PointSize = uPointSize;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    private string FragmentColor() => Header() + """
        varying vec4 vColor;
        void main() { gl_FragColor = vColor; }
        """;

    // Vertex shader for translated MilkDrop2 shaders: feeds the fragment stage both the (possibly warped)
    // sample UV and the original screen position (uv_orig) that the warp/comp shaders expect.
    private string ShaderVertex() => Header() + """
        attribute vec2 aPos;
        attribute vec2 aUv;
        varying vec2 vUv;
        varying vec2 vUvOrig;
        void main() {
            vUv = aUv;
            vUvOrig = aPos * 0.5 + 0.5;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    // The mesh vertices already carry the warped source UV (computed by the engine's per-pixel pass),
    // so the fragment shader just samples the previous frame and applies the decay fade.
    private string FragmentMesh() => Header() + """
        uniform sampler2D uTex;
        uniform float uDecay;
        varying vec2 vUv;
        void main() {
            vec4 col = texture2D(uTex, vUv);
            gl_FragColor = vec4(col.rgb * uDecay, 1.0);
        }
        """;

    private string FragmentBlit() => Header() + """
        uniform sampler2D uTex;
        varying vec2 vUv;
        void main() { gl_FragColor = texture2D(uTex, vUv); }
        """;

    // Present a layer's output texture with a uniform alpha so the incoming preset can fade in.
    private string FragmentFade() => Header() + """
        uniform sampler2D uTex;
        uniform float uAlpha;
        varying vec2 vUv;
        void main() {
            vec4 col = texture2D(uTex, vUv);
            gl_FragColor = vec4(col.rgb, col.a * uAlpha);
        }
        """;

    private string FragmentSolid() => Header() + """
        uniform vec4 uColor;
        void main() { gl_FragColor = uColor; }
        """;

    // 5-tap separable gaussian (Sigma-ish kernel from the classic "efficient gaussian blur" weights).
    // uDir is the per-texel step along one axis; the pass is run once horizontally and once vertically.
    private string FragmentBlur() => Header() + """
        uniform sampler2D uTex;
        uniform vec2 uDir;
        varying vec2 vUv;
        void main() {
            vec4 c = texture2D(uTex, vUv) * 0.2270270270;
            c += texture2D(uTex, vUv + uDir * 1.3846153846) * 0.3162162162;
            c += texture2D(uTex, vUv - uDir * 1.3846153846) * 0.3162162162;
            c += texture2D(uTex, vUv + uDir * 3.2307692308) * 0.0702702703;
            c += texture2D(uTex, vUv - uDir * 3.2307692308) * 0.0702702703;
            gl_FragColor = c;
        }
        """;
}
