using System;
using System.Globalization;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Plugin;

/// <summary>
/// The GPU half of the window-resolution Alchemy: the feedback field at device resolution, and the
/// passes that advance it one frame. It knows nothing about Avalonia. It needs a current GL context and
/// a <see cref="GlBindings"/>, so the harness can run the very same passes offscreen and check them
/// against the CPU port.
///
/// The field lives in three device-sized RGBA8 textures plus a scratch one. Row r of every texture is
/// field row r, so row 0 (the top of the picture) sits at the BOTTOM of the texture, and only
/// <see cref="Present"/> flips. Each frame:
/// <list type="number">
/// <item><b>Gather.</b> Every pixel copies the previous frame from the warp's source pixel. That is
/// <see cref="WarpMap.Build"/> (plus <see cref="WarpMap.Morph"/> during a morph) evaluated on the fly in
/// device pixels. Sampling is nearest, and the kernels truncate to whole pixels as the original does, so
/// nothing is resampled.</item>
/// <item><b>Diffuse.</b> <see cref="FeedbackPass"/>'s <c>(3·avg4 + centre) / 4</c>, with its two integer
/// truncations, which are the only thing that makes the trails fade. At scale 1 it runs exactly. At
/// other scales the four-neighbour average is replaced by a Gaussian with the same SPREAD in field
/// units (variance 0.5·s² device px² per axis), so trails soften at the original's rate.</item>
/// <item><b>Edges.</b> The top and bottom rows go black (one field unit tall).</item>
/// <item><b>Overlay.</b> The recorded strokes and discs, blended in the order they were drawn.</item>
/// </list>
/// </summary>
internal sealed class AlchemyGlRenderer
{
    /// <summary>The widest Gaussian the diffusion shader can apply (taps either side).</summary>
    private const int MaxBlurRadius = 24;

    private readonly GlBindings _gl;
    private readonly bool _isGles;

    private int _gatherProgram, _exactBlurProgram, _hBlurProgram, _vBlurProgram;
    private int _strokeProgram, _discProgram, _copyProgram;

    // Gather.
    private int _gPos, _gPrev, _gRes, _gWarp, _gKinds, _gFlags, _gMorph, _gDebug, _gBias;
    // Exact diffusion.
    private int _ePos, _eScratch, _ePrev, _eRes;
    // Gaussian diffusion: horizontal, then vertical + combine.
    private int _hPos, _hSrc, _hRes, _hWeights, _hRadius;
    private int _vPos, _vSrc, _vScratch, _vRes, _vWeights, _vRadius, _vCentre, _vEdge, _vUnit;
    // Strokes.
    private int _sPos, _sAcross, _sColor, _sProfile, _sClip, _sScale, _sDevice, _sField;
    // Discs.
    private int _dCorner, _dScale, _dDevice, _dDisc, _dFill, _dRim, _dPass;
    // Copy / present.
    private int _cPos, _cTex, _cFlip;

    private int _quadVbo, _cornerVbo, _strokeVbo;

    private int _texA, _fboA, _texB, _fboB, _texS, _fboS, _texT, _fboT;

    private readonly float[] _warp = new float[24 * 4];
    private readonly float[] _kinds = new float[2 * 4];
    private readonly float[] _flags = new float[2 * 4];
    private readonly float[] _gauss = new float[MaxBlurRadius + 1];
    private int _gaussRadius;
    private float _gaussCentre;

    public AlchemyGlRenderer(GlBindings gl, bool isGles)
    {
        _gl = gl;
        _isGles = isGles;
    }

    /// <summary>The current field geometry; default until <see cref="EnsureSize"/>.</summary>
    public AlchemyFieldScale Scale { get; private set; }

    /// <summary>The texture holding the latest frame.</summary>
    public int FrameTexture => _texA;

    public void Init()
    {
        var g = _gl;

        _gatherProgram = g.BuildProgram(VertexQuad(), FragmentGather());
        _gPos = g.GetAttrib(_gatherProgram, "aPos");
        _gPrev = g.GetUniform(_gatherProgram, "uPrev");
        _gRes = g.GetUniform(_gatherProgram, "uRes");
        _gWarp = UniformArray(_gatherProgram, "uW");
        _gKinds = UniformArray(_gatherProgram, "uKinds");
        _gFlags = UniformArray(_gatherProgram, "uFlags");
        _gMorph = g.GetUniform(_gatherProgram, "uMorph");
        _gDebug = g.GetUniform(_gatherProgram, "uDebugSource");
        _gBias = g.GetUniform(_gatherProgram, "uBias");

        _exactBlurProgram = g.BuildProgram(VertexQuad(), FragmentExactBlur());
        _ePos = g.GetAttrib(_exactBlurProgram, "aPos");
        _eScratch = g.GetUniform(_exactBlurProgram, "uScratch");
        _ePrev = g.GetUniform(_exactBlurProgram, "uPrev");
        _eRes = g.GetUniform(_exactBlurProgram, "uRes");

        _hBlurProgram = g.BuildProgram(VertexQuad(), FragmentHBlur());
        _hPos = g.GetAttrib(_hBlurProgram, "aPos");
        _hSrc = g.GetUniform(_hBlurProgram, "uSrc");
        _hRes = g.GetUniform(_hBlurProgram, "uRes");
        _hWeights = UniformArray(_hBlurProgram, "uGauss");
        _hRadius = g.GetUniform(_hBlurProgram, "uRadius");

        _vBlurProgram = g.BuildProgram(VertexQuad(), FragmentVBlur());
        _vPos = g.GetAttrib(_vBlurProgram, "aPos");
        _vSrc = g.GetUniform(_vBlurProgram, "uSrc");
        _vScratch = g.GetUniform(_vBlurProgram, "uScratch");
        _vRes = g.GetUniform(_vBlurProgram, "uRes");
        _vWeights = UniformArray(_vBlurProgram, "uGauss");
        _vRadius = g.GetUniform(_vBlurProgram, "uRadius");
        _vCentre = g.GetUniform(_vBlurProgram, "uCentre");
        _vEdge = g.GetUniform(_vBlurProgram, "uEdge");
        _vUnit = g.GetUniform(_vBlurProgram, "uUnit");

        _strokeProgram = g.BuildProgram(VertexStroke(), FragmentStroke());
        _sPos = g.GetAttrib(_strokeProgram, "aPos");
        _sAcross = g.GetAttrib(_strokeProgram, "aAcross");
        _sColor = g.GetAttrib(_strokeProgram, "aColor");
        _sProfile = g.GetAttrib(_strokeProgram, "aProfile");
        _sClip = g.GetAttrib(_strokeProgram, "aClip");
        _sScale = g.GetUniform(_strokeProgram, "uScale");
        _sDevice = g.GetUniform(_strokeProgram, "uDevice");
        _sField = g.GetUniform(_strokeProgram, "uField");

        _discProgram = g.BuildProgram(VertexDisc(), FragmentDisc());
        _dCorner = g.GetAttrib(_discProgram, "aCorner");
        _dScale = g.GetUniform(_discProgram, "uScale");
        _dDevice = g.GetUniform(_discProgram, "uDevice");
        _dDisc = g.GetUniform(_discProgram, "uDisc");
        _dFill = g.GetUniform(_discProgram, "uFill");
        _dRim = g.GetUniform(_discProgram, "uRim");
        _dPass = g.GetUniform(_discProgram, "uPass");

        _copyProgram = g.BuildProgram(VertexCopy(), FragmentCopy());
        _cPos = g.GetAttrib(_copyProgram, "aPos");
        _cTex = g.GetUniform(_copyProgram, "uTex");
        _cFlip = g.GetUniform(_copyProgram, "uFlip");

        _quadVbo = g.GenBuffer();
        g.BindBuffer(GlBindings.ArrayBuffer, _quadVbo);
        g.BufferData(GlBindings.ArrayBuffer, [-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f], GlBindings.StaticDraw);

        _cornerVbo = g.GenBuffer();
        g.BindBuffer(GlBindings.ArrayBuffer, _cornerVbo);
        g.BufferData(GlBindings.ArrayBuffer, [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f], GlBindings.StaticDraw);

        _strokeVbo = g.GenBuffer();
    }

    public void Dispose()
    {
        var g = _gl;
        foreach (var p in new[] { _gatherProgram, _exactBlurProgram, _hBlurProgram, _vBlurProgram, _strokeProgram, _discProgram, _copyProgram })
            if (p != 0) g.DeleteProgram(p);
        foreach (var b in new[] { _quadVbo, _cornerVbo, _strokeVbo })
            if (b != 0) g.DeleteBuffer(b);
        DeleteTargets();
    }

    // Some drivers expose an array uniform under "name[0]" rather than "name"; try both.
    private int UniformArray(int program, string name)
    {
        var loc = _gl.GetUniform(program, name + "[0]");
        return loc >= 0 ? loc : _gl.GetUniform(program, name);
    }

    /// <summary>
    /// (Re)allocate the field for <paramref name="scale"/>. An existing field is stretched into the new
    /// one rather than cleared, so resizing the window does not flash to black.
    /// </summary>
    public void EnsureSize(AlchemyFieldScale scale)
    {
        if (scale == Scale && _texA != 0) return;
        var w = scale.DeviceWidth;
        var h = scale.DeviceHeight;

        var (newA, newFboA) = CreateTarget(w, h);
        if (_texA != 0)
        {
            _gl.BindFramebuffer(newFboA);
            _gl.Viewport(0, 0, w, h);
            DrawCopy(_texA, flip: false);
        }

        DeleteTargets();
        (_texA, _fboA) = (newA, newFboA);
        (_texB, _fboB) = CreateTarget(w, h);
        (_texS, _fboS) = CreateTarget(w, h);
        (_texT, _fboT) = CreateTarget(w, h);
        Scale = scale;
        BuildGaussian(scale.Scale);
    }

    /// <summary>
    /// The diffusion kernel for a scale. The original's step is <c>(3·M + c)/4</c>, where M averages the
    /// four neighbours. M's variance is 0.5 px² per axis, so the step spreads content by 0.375 px² a
    /// frame. Here M becomes a Gaussian with its centre tap removed. That shape has variance
    /// σ²/(1 − g0), where g0 is the 2-D centre weight, and σ is solved so it equals 0.5·s².
    /// </summary>
    private void BuildGaussian(double s)
    {
        var target = 0.5 * s * s;
        double lo = 0.05, hi = Math.Max(1.0, s * 2);
        double[] weights = [];
        double centre = 0;
        for (var iter = 0; iter < 60; iter++)
        {
            var sigma = (lo + hi) / 2;
            weights = Kernel(sigma, out var variance);
            centre = weights[0] * weights[0];
            if (variance / (1 - centre) < target) lo = sigma;
            else hi = sigma;
        }

        Array.Clear(_gauss);
        _gaussRadius = weights.Length - 1;
        for (var i = 0; i < weights.Length; i++) _gauss[i] = (float)weights[i];
        _gaussCentre = (float)centre;
    }

    /// <summary>A normalised 1-D Gaussian, tap 0 first; <paramref name="variance"/> is its discrete variance.</summary>
    private static double[] Kernel(double sigma, out double variance)
    {
        var radius = Math.Clamp((int)Math.Ceiling(sigma * 3), 1, MaxBlurRadius);
        var w = new double[radius + 1];
        var sum = 0.0;
        for (var i = 0; i <= radius; i++)
        {
            w[i] = Math.Exp(-(i * i) / (2 * sigma * sigma));
            sum += i == 0 ? w[i] : 2 * w[i];
        }
        variance = 0;
        for (var i = 0; i <= radius; i++)
        {
            w[i] /= sum;
            variance += 2 * w[i] * i * i;
        }
        return w;
    }

    /// <summary>Advance the field one frame: gather, diffuse, edges, overlay.</summary>
    /// <param name="drawOverlay">False skips the strokes and discs (harness: checks the feedback alone).</param>
    public void Step(AlchemyGpuFrame frame, GpuWarpScaler warps, bool drawOverlay = true)
    {
        var g = _gl;
        var scale = Scale;
        var w = scale.DeviceWidth;
        var h = scale.DeviceHeight;
        g.Disable(GlBindings.Blend);

        if (!frame.HasWarp)
        {
            // No kernel, no feedback pass: the field stays as it was and only the overlay lands on it.
            g.BindFramebuffer(_fboA);
            g.Viewport(0, 0, w, h);
            if (drawOverlay) DrawStrokes(frame.Strokes);
            return;
        }

        // 1. Gather the previous frame through the warp into the scratch target.
        Gather(frame, warps, debugSource: false);

        // 2-3. Diffuse into the other field target, with the black edge rows.
        if (scale.IsExact)
        {
            g.BindFramebuffer(_fboB);
            UseProgram(_exactBlurProgram);
            BindQuad(_ePos);
            BindTexture(0, _texS, _eScratch);
            BindTexture(1, _texA, _ePrev);
            g.Uniform2f(_eRes, w, h);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
        }
        else
        {
            g.BindFramebuffer(_fboT);
            UseProgram(_hBlurProgram);
            BindQuad(_hPos);
            BindTexture(0, _texS, _hSrc);
            g.Uniform2f(_hRes, w, h);
            g.Uniform1fv(_hWeights, _gauss);
            g.Uniform1i(_hRadius, _gaussRadius);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);

            g.BindFramebuffer(_fboB);
            UseProgram(_vBlurProgram);
            BindQuad(_vPos);
            BindTexture(0, _texT, _vSrc);
            BindTexture(1, _texS, _vScratch);
            g.Uniform2f(_vRes, w, h);
            g.Uniform1fv(_vWeights, _gauss);
            g.Uniform1i(_vRadius, _gaussRadius);
            g.Uniform1f(_vCentre, _gaussCentre);
            var unit = Math.Max(1f, (float)Math.Round(scale.Scale));
            g.Uniform1f(_vEdge, unit);
            g.Uniform1f(_vUnit, unit);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
        }

        // 4. The overlay, in draw order.
        if (drawOverlay) DrawStrokes(frame.Strokes);

        (_texA, _texB) = (_texB, _texA);
        (_fboA, _fboB) = (_fboB, _fboA);
    }

    private void Gather(AlchemyGpuFrame frame, GpuWarpScaler warps, bool debugSource)
    {
        var g = _gl;
        g.BindFramebuffer(_fboS);
        g.Viewport(0, 0, Scale.DeviceWidth, Scale.DeviceHeight);
        UseProgram(_gatherProgram);
        BindQuad(_gPos);
        BindTexture(0, _texA, _gPrev);
        g.Uniform2f(_gRes, Scale.DeviceWidth, Scale.DeviceHeight);
        PackWarp(warps.Current, 0);
        PackWarp(warps.From, 1);
        g.Uniform4fv(_gWarp, 24, _warp);
        g.Uniform4fv(_gKinds, 2, _kinds);
        g.Uniform4fv(_gFlags, 2, _flags);
        // The morph's multiplier is formed in float and applied in double; see WarpMap.Morph.
        var factor = frame.Morphing ? (float)(frame.MorphIndex + 1) * (float)(1.0 / 23) : 0f;
        g.Uniform2f(_gMorph, frame.Morphing ? 1f : 0f, factor);
        g.Uniform1f(_gDebug, debugSource ? 1f : 0f);
        g.Uniform1f(_gBias, Scale.IsExact ? 0f : (float)((Scale.Scale - 1) / 2));
        g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
    }

    /// <summary>
    /// Harness: the warp's source pixel for every destination, as RGBA bytes: x = R·256 + G and
    /// y = B·256 + A. The field itself is left untouched.
    /// </summary>
    public void ReadWarpSources(AlchemyGpuFrame frame, GpuWarpScaler warps, byte[] rgba)
    {
        _gl.Disable(GlBindings.Blend);
        Gather(frame, warps, debugSource: true);
        _gl.ReadPixelsRgba(0, 0, Scale.DeviceWidth, Scale.DeviceHeight, rgba);
    }

    /// <summary>Draw the latest frame to <paramref name="fb"/>, top row up.</summary>
    public void Present(int fb, int width, int height)
    {
        _gl.BindFramebuffer(fb);
        _gl.Viewport(0, 0, width, height);
        _gl.Disable(GlBindings.Blend);
        DrawCopy(_texA, flip: true);
    }

    /// <summary>Black out the field.</summary>
    public void Clear()
    {
        _gl.BindFramebuffer(_fboA);
        _gl.Viewport(0, 0, Scale.DeviceWidth, Scale.DeviceHeight);
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(GlBindings.ColorBufferBit);
    }

    /// <summary>Harness: replace the latest frame with <paramref name="rgba"/>, field row 0 first.</summary>
    public void UploadFrame(byte[] rgba)
    {
        _gl.BindTexture(GlBindings.Texture2D, _texA);
        _gl.TexImageRgba(Scale.DeviceWidth, Scale.DeviceHeight, rgba);
    }

    /// <summary>Harness: read the latest frame, field row 0 first.</summary>
    public void ReadFrame(byte[] rgba)
    {
        _gl.BindFramebuffer(_fboA);
        _gl.ReadPixelsRgba(0, 0, Scale.DeviceWidth, Scale.DeviceHeight, rgba);
    }

    private void PackWarp(GpuWarpSet set, int index)
    {
        var o = index * 48;
        Array.Copy(set.A.Params, 0, _warp, o, 12);
        Array.Copy(set.A.ChildParams, 0, _warp, o + 12, 12);
        Array.Copy(set.B.Params, 0, _warp, o + 24, 12);
        Array.Copy(set.B.ChildParams, 0, _warp, o + 36, 12);
        var k = index * 4;
        _kinds[k] = (float)set.A.Kind;
        _kinds[k + 1] = (float)set.A.ChildKind;
        _kinds[k + 2] = set.HasB ? (float)set.B.Kind : 0f;
        _kinds[k + 3] = set.HasB ? (float)set.B.ChildKind : 0f;
        _flags[k] = set.HasB ? 1f : 0f;
        _flags[k + 1] = set.A.ToOrigin ? 1f : 0f;
        _flags[k + 2] = set.B.ToOrigin ? 1f : 0f;
        _flags[k + 3] = 0f;
    }

    private void DrawStrokes(AlchemyStrokeList strokes)
    {
        if (strokes.Batches.Count == 0) return;
        var g = _gl;
        var scale = Scale;
        g.Enable(GlBindings.Blend);
        g.BlendFunc(GlBindings.SrcAlpha, GlBindings.OneMinusSrcAlpha);

        var uploaded = false;
        foreach (var batch in strokes.Batches)
        {
            if (!batch.IsDisc)
            {
                UseProgram(_strokeProgram);
                g.BindBuffer(GlBindings.ArrayBuffer, _strokeVbo);
                if (!uploaded)
                {
                    g.BufferDataRange(GlBindings.ArrayBuffer, strokes.Vertices,
                                      strokes.VertexCount * AlchemyStrokeList.FloatsPerVertex, GlBindings.DynamicDraw);
                    uploaded = true;
                }
                const int stride = AlchemyStrokeList.FloatsPerVertex;
                Attrib(_sPos, 2, stride, 0);
                Attrib(_sAcross, 1, stride, 2);
                Attrib(_sColor, 3, stride, 3);
                Attrib(_sProfile, 3, stride, 6);
                Attrib(_sClip, 1, stride, 9);
                g.Uniform1f(_sScale, (float)scale.Scale);
                g.Uniform2f(_sDevice, scale.DeviceWidth, scale.DeviceHeight);
                g.Uniform2f(_sField, scale.FieldWidth, scale.FieldHeight);
                g.DrawArrays(GlBindings.Triangles, batch.Start, batch.Count);
                continue;
            }

            var d = batch.Disc;
            UseProgram(_discProgram);
            g.BindBuffer(GlBindings.ArrayBuffer, _cornerVbo);
            Attrib(_dCorner, 2, 2, 0);
            g.Uniform1f(_dScale, (float)scale.Scale);
            g.Uniform2f(_dDevice, scale.DeviceWidth, scale.DeviceHeight);
            g.Uniform4f(_dDisc, d.Cx, d.Cy, d.Radius, d.Alpha);
            g.Uniform3f(_dFill, ((d.Fill >> 16) & 0xFF) / 255f, ((d.Fill >> 8) & 0xFF) / 255f, (d.Fill & 0xFF) / 255f);
            g.Uniform3f(_dRim, ((d.Rim >> 16) & 0xFF) / 255f, ((d.Rim >> 8) & 0xFF) / 255f, (d.Rim & 0xFF) / 255f);
            // Core then rim. Each pixel takes the core's blend before the rim's, as in the original.
            g.Uniform1f(_dPass, 0f);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
            g.Uniform1f(_dPass, 1f);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
        }

        g.Disable(GlBindings.Blend);
    }

    private void DrawCopy(int texture, bool flip)
    {
        UseProgram(_copyProgram);
        BindQuad(_cPos);
        BindTexture(0, texture, _cTex);
        _gl.Uniform1f(_cFlip, flip ? 1f : 0f);
        _gl.DrawArrays(GlBindings.TriangleStrip, 0, 4);
    }

    // Attribute arrays enabled for the previous program are switched off first, so a draw never pulls
    // from an array the current program does not use.
    private readonly bool[] _enabled = new bool[16];

    private void UseProgram(int program)
    {
        for (var i = 0; i < _enabled.Length; i++)
        {
            if (!_enabled[i]) continue;
            _gl.DisableVertexAttrib(i);
            _enabled[i] = false;
        }
        _gl.UseProgram(program);
    }

    private void Attrib(int location, int size, int stride, int offset)
    {
        if (location < 0) return;
        _gl.EnableVertexAttrib(location);
        if (location < _enabled.Length) _enabled[location] = true;
        _gl.VertexAttribFloat(location, size, stride, offset);
    }

    private void BindQuad(int location)
    {
        _gl.BindBuffer(GlBindings.ArrayBuffer, _quadVbo);
        Attrib(location, 2, 2, 0);
    }

    private void BindTexture(int unit, int texture, int uniform)
    {
        _gl.ActiveTexture(GlBindings.Texture0 + unit);
        _gl.BindTexture(GlBindings.Texture2D, texture);
        _gl.Uniform1i(uniform, unit);
    }

    private (int Texture, int Fbo) CreateTarget(int w, int h)
    {
        var g = _gl;
        var tex = g.GenTexture();
        g.ActiveTexture(GlBindings.Texture0);
        g.BindTexture(GlBindings.Texture2D, tex);
        g.TexImageEmpty(w, h);
        // NEAREST throughout: every pass addresses whole pixels.
        g.TexParameter(GlBindings.TextureMinFilter, GlBindings.Nearest);
        g.TexParameter(GlBindings.TextureMagFilter, GlBindings.Nearest);
        g.TexParameter(GlBindings.TextureWrapS, GlBindings.ClampToEdge);
        g.TexParameter(GlBindings.TextureWrapT, GlBindings.ClampToEdge);

        var fbo = g.GenFramebuffer();
        g.BindFramebuffer(fbo);
        g.AttachColorTexture(tex);
        g.ClearColor(0, 0, 0, 1);
        g.Clear(GlBindings.ColorBufferBit);
        return (tex, fbo);
    }

    private void DeleteTargets()
    {
        foreach (var t in new[] { _texA, _texB, _texS, _texT })
            if (t != 0) _gl.DeleteTexture(t);
        foreach (var f in new[] { _fboA, _fboB, _fboS, _fboT })
            if (f != 0) _gl.DeleteFramebuffer(f);
        _texA = _texB = _texS = _texT = 0;
        _fboA = _fboB = _fboS = _fboT = 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Shaders. GLSL 1.20 / GLSL ES 1.00, so they run on desktop GL and on ANGLE alike. Every pass
    // works in device pixels addressed by gl_FragCoord, and integers are carried in floats (exact up to
    // 2^24, far beyond any window).

    private static readonly string RimLimit = MpvisMath.DiscRimLimit.ToString("R", CultureInfo.InvariantCulture);

    private string Header() => _isGles ? "#version 100\nprecision highp float;\n" : "#version 120\n";

    private string VertexQuad() => Header() + """
        attribute vec2 aPos;
        void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
        """;

    /// <summary>Helpers shared by the field passes: whole-pixel reads and the original's roundings.</summary>
    private const string FieldHelpers = """
        const float PI = 3.14159265358979;
        // (int) casts truncate toward zero; floor() differs for negatives.
        float trunc1(float v) { return v < 0.0 ? -floor(-v) : floor(v); }
        // MpvisRound: half away from zero, with exactly zero taking the negative branch.
        float mpround(float v) { return v <= 0.0 ? trunc1(v - 0.5) : trunc1(v + 0.5); }
        // A non-negative modulo that stays exact for whole numbers even if the division is approximate.
        float imod(float x, float n) {
            float m = x - n * floor((x + 0.5) / n);
            if (m < 0.0) m += n;
            if (m >= n) m -= n;
            return m;
        }
        vec3 fetch(sampler2D tex, vec2 px, vec2 res) { return texture2D(tex, (px + 0.5) / res).rgb; }
        // 0..255 integers per channel.
        vec3 fetchInt(sampler2D tex, vec2 px, vec2 res) { return floor(fetch(tex, px, res) * 255.0 + 0.5); }
        """;

    /// <summary>
    /// The gather: <see cref="WarpMap.Build"/>'s per-pixel transform, kernel by kernel, in device pixels.
    /// Parameter layouts are <see cref="GpuWarpScaler"/>'s.
    /// </summary>
    private string FragmentGather() => Header() + FieldHelpers + """
        uniform sampler2D uPrev;
        uniform vec2 uRes;
        // Two warp sets (current, morph-from), 12 vec4 each: A params, A child, B params, B child.
        uniform vec4 uW[24];
        // Per set: kinds (A, A child, B, B child) and flags (hasB, A to-origin, B to-origin, -).
        uniform vec4 uKinds[2];
        uniform vec4 uFlags[2];
        // (morphing, factor)
        uniform vec2 uMorph;
        uniform float uDebugSource;
        // (s - 1) / 2: see ftrunc.
        uniform float uBias;

        // A truncation toward zero that the original makes at FIELD-pixel granularity. Rounding a
        // coordinate toward zero is not neutral: on average it pulls the source half a pixel toward the
        // kernel's origin, every frame, and that pull is part of how the original flows (it is what holds
        // a Stretch's centre still, for one). Truncating device pixels instead would pull only half a
        // device pixel, a third of the original's at a scale of 3. So pull by (s - 1)/2 first and then
        // truncate: on average the pull is s/2 device pixels, half a field pixel again, and nothing is
        // quantized to the coarse grid. At a scale of 1 this is exactly trunc().
        float ftrunc(float v) {
            float a = abs(v) - uBias;
            return a <= 0.0 ? 0.0 : (v < 0.0 ? -floor(a) : floor(a));
        }

        float angleOf(vec2 d) { return (d.x == 0.0 && d.y == 0.0) ? 0.0 : atan(d.y, d.x); }

        vec2 warpLinear(vec2 p, vec4 A, vec4 B, vec4 C) {
            vec2 q = p;
            if (A.x < 0.5) {
                q = p + A.yz;
            } else {
                float zx = A.w, zy = B.x;
                float dir = floor(B.y + 0.5);
                bool xLeft  = dir == 0.0 || dir == 4.0 || dir == 6.0;
                bool xRight = dir == 2.0 || dir == 5.0 || dir == 7.0;
                bool yZoom  = dir == 1.0 || dir == 4.0 || dir == 7.0;
                bool yFar   = dir == 3.0 || dir == 5.0 || dir == 6.0;
                if (xLeft) q.x = ftrunc(zx * p.x);
                else if (xRight) { float d = (uRes.x - p.x) - 1.0; q.x = p.x - ftrunc(zx * d - d); }
                if (yZoom) q.y = ftrunc(zy * p.y);
                else if (yFar) { float d = (uRes.y - p.y) - 1.0; q.y = p.y - ftrunc(zy * d - d); }
            }
            float shake = floor(B.z + 0.5);
            if (shake == 1.0) q.x += mpround(sin(q.y / uRes.y * B.w * PI) * C.x);
            else if (shake >= 2.0) q.y += mpround(sin(q.x / uRes.x * B.w * PI) * C.y);
            return q;
        }

        vec2 warpSnafu(vec2 p, vec4 A) {
            float width = A.y, speed = A.z;
            float period = 2.0 * width;
            float mx = imod(p.x, period);
            float my = imod(p.y, period);
            vec2 q = p;
            if (A.x > 0.5) {
                q.y = mx <= width ? p.y - speed : p.y + speed;
            } else if (my > width) {
                if (width <= uRes.x - p.x || mx <= my - width) q.x = p.x + speed;
                else q.y = p.y + speed;
            } else {
                if (width <= p.x || mx <= my) q.x = p.x - speed;
                else q.y = p.y + speed;
            }
            return q;
        }

        vec2 warpStretch(vec2 p, vec4 A, vec4 B) {
            vec2 c = A.xy;
            vec2 d = p - c;
            float theta = angleOf(d);
            float r = length(d);
            float t = r / A.w;
            float rr = r - t * t * t * A.z;
            // Without SinShake the twist term is the NORMALISED radius.
            float term = B.y > 0.5 ? sin(B.z * t * PI) : t;
            float ang = term * B.x + theta;
            // Truncate the rotated offset BEFORE adding the whole-pixel centre.
            return vec2(ftrunc(cos(ang) * rr) + c.x, ftrunc(sin(ang) * rr) + c.y);
        }

        vec2 child(float kind, vec2 p, vec4 CA, vec4 CB, vec4 CC, vec2 focal, float spin) {
            if (kind == 1.0) return warpLinear(p, CA, CB, CC);
            if (kind == 2.0) return warpSnafu(p, CA);
            if (kind == 3.0) return warpStretch(p, CA, CB);
            // ShiftMode 3: point reflection through the centre, rotated by the spin.
            vec2 d = focal - p;
            float r = length(d);
            float theta = angleOf(d) + spin;
            return vec2((uRes.x - focal.x) - ftrunc(cos(theta) * r),
                        (uRes.y - focal.y) - ftrunc(sin(theta) * r));
        }

        // The mirror of reflection modes 0 and 2. True when the child should run.
        bool mirror(inout vec2 p, vec2 focal) {
            bool xInside = p.x <= focal.x;
            if (!xInside) p.x = uRes.x - p.x;
            if (p.y > focal.y) { p.y = uRes.y - p.y; return false; }
            return xInside;
        }

        // A = (reflectionMode, shiftMode, spin, centerR), B = (littleR, boxSize, focalX, focalY),
        // C = (halfDiagonal, tile period, tile scale, -).
        vec2 warpOScope(vec2 p, vec4 A, vec4 B, vec4 C, float kind, vec4 CA, vec4 CB, vec4 CC) {
            float mode = floor(A.x + 0.5);
            float spin = A.z, centerR = A.w, boxSize = B.y;
            vec2 focal = B.zw;
            vec2 q = p;

            if (mode == 0.0) {
                if (mirror(q, focal)) return child(kind, q, CA, CB, CC, focal, spin);
                return q;
            }
            if (mode == 1.0) {
                // Four-wedge diagonal fold, by integer cross-products against both diagonals.
                float wy = uRes.x * q.y;
                float anti = (uRes.x - q.x) * uRes.y;
                if (wy < uRes.y * q.x) {
                    if (wy < anti) {
                        if (q.x < focal.x) return child(kind, q, CA, CB, CC, focal, spin);
                        return vec2(focal.x * 2.0 - q.x, q.y);
                    }
                    return vec2((uRes.x - q.x) + focal.x, abs(q.y - focal.y));
                }
                if (anti <= wy) {
                    q.y = uRes.y - q.y;
                    if (q.x <= focal.x) return q;
                    return vec2(focal.x * 2.0 - q.x, q.y);
                }
                return vec2(focal.x - q.x, abs(q.y - focal.y));
            }
            if (mode == 2.0) {
                vec2 d = q - focal;
                float theta = angleOf(d);
                float r = length(d);
                float foldR = C.x * 0.5;
                // An INTEGER division in the original: it trips once the truncated radius reaches the
                // truncated boundary. No rotation is applied.
                float foldInt = floor(foldR);
                if (foldInt != 0.0 && floor(r) >= foldInt) {
                    float rr = r - 2.0 * (r - foldR);
                    return vec2(ftrunc(cos(theta) * rr) + focal.x, ftrunc(sin(theta) * rr) + focal.y);
                }
                if (mirror(q, focal)) return child(kind, q, CA, CB, CC, focal, spin);
                return q;
            }
            if (mode == 3.0) {
                if (centerR >= length(focal - q)) return child(kind, q, CA, CB, CC, focal, spin);
                float ix = imod(q.x, C.y);
                float iy = imod(q.y, C.y);
                return vec2(mpround(ix * C.z) + (focal.x - centerR), mpround(iy * C.z) + (focal.y - centerR));
            }
            // Tile gate: a coordinate in an odd tile on either axis passes straight through.
            if (boxSize != 0.0) {
                if (imod(floor((q.x + 0.5) / boxSize), 2.0) != 0.0) return q;
                if (imod(floor((q.y + 0.5) / boxSize), 2.0) != 0.0) return q;
            }
            return child(kind, q, CA, CB, CC, focal, spin);
        }

        vec2 doWarp(float kind, float childKind, vec2 p, vec4 P0, vec4 P1, vec4 P2, vec4 C0, vec4 C1, vec4 C2) {
            if (kind == 1.0) return warpLinear(p, P0, P1, P2);
            if (kind == 2.0) return warpSnafu(p, P0);
            if (kind == 3.0) return warpStretch(p, P0, P1);
            if (kind == 4.0) return warpOScope(p, P0, P1, P2, childKind, C0, C1, C2);
            return p;
        }

        // WarpMap.Resolve: an escaped coordinate stands still, or samples the fixed pixel (0,0).
        float resolve1(float v, float incoming, float n, float toOrigin) {
            if (v >= 0.0 && v < n) return v;
            return toOrigin > 0.5 ? 0.0 : incoming;
        }
        vec2 resolve(vec2 v, vec2 incoming, float toOrigin) {
            return vec2(resolve1(v.x, incoming.x, uRes.x, toOrigin), resolve1(v.y, incoming.y, uRes.y, toOrigin));
        }

        vec2 currentSource(vec2 dest) {
            vec4 k = uKinds[0];
            vec4 f = uFlags[0];
            vec2 a = doWarp(floor(k.x + 0.5), floor(k.y + 0.5), dest, uW[0], uW[1], uW[2], uW[3], uW[4], uW[5]);
            a = resolve(a, dest, f.y);
            if (f.x < 0.5) return a;
            vec2 b = doWarp(floor(k.z + 0.5), floor(k.w + 0.5), a, uW[6], uW[7], uW[8], uW[9], uW[10], uW[11]);
            b = resolve(b, a, f.z);
            return floor((b + dest) * 0.5 - uBias * 0.5);
        }

        // trunc(d * f) as the CPU computes it, in DOUBLE: d is whole and f a float, so the exact product
        // needs ~36 bits. The float product can round onto an integer the exact one stays just short of
        // (23 * float(3/23) is 2.99999997, which float rounds to 3), and a morph then shifts whole rows.
        // The residual of the candidate is formed exactly from three 12-bit slices of f and only its
        // sign is used.
        float morphStep(float d, float f) {
            if (uBias > 0.0) return ftrunc(d * f);
            float q = trunc1(d * f);
            float fh = floor(f * 4096.0) / 4096.0;
            float fm = floor((f - fh) * 16777216.0) / 16777216.0;
            float fl = (f - fh) - fm;
            float r = ((d * fh - q) + d * fm) + d * fl;
            if (d * f >= 0.0) { if (r < 0.0) q -= 1.0; }
            else if (r > 0.0) q += 1.0;
            return q;
        }

        vec2 fromSource(vec2 dest) {
            vec4 k = uKinds[1];
            vec4 f = uFlags[1];
            vec2 a = doWarp(floor(k.x + 0.5), floor(k.y + 0.5), dest, uW[12], uW[13], uW[14], uW[15], uW[16], uW[17]);
            a = resolve(a, dest, f.y);
            if (f.x < 0.5) return a;
            vec2 b = doWarp(floor(k.z + 0.5), floor(k.w + 0.5), a, uW[18], uW[19], uW[20], uW[21], uW[22], uW[23]);
            b = resolve(b, a, f.z);
            return floor((b + dest) * 0.5 - uBias * 0.5);
        }

        void main() {
            vec2 dest = floor(gl_FragCoord.xy);
            vec2 src = currentSource(dest);
            if (uMorph.x > 0.5) {
                // WarpMap.Morph: old + trunc((new - old) * factor), per axis.
                vec2 from = fromSource(dest);
                src = from + vec2(morphStep(src.x - from.x, uMorph.y), morphStep(src.y - from.y, uMorph.y));
            }
            if (uDebugSource > 0.5) {
                gl_FragColor = vec4(floor(src.x / 256.0), mod(src.x, 256.0), floor(src.y / 256.0), mod(src.y, 256.0)) / 255.0;
                return;
            }
            gl_FragColor = vec4(fetch(uPrev, src, uRes), 1.0);
        }
        """;

    /// <summary>
    /// <see cref="FeedbackPass"/> exactly, for a scale of 1: the flat walk's wrap at the side columns,
    /// both truncations, and the edge rows, whose last pixel keeps the previous frame's value.
    /// </summary>
    private string FragmentExactBlur() => Header() + FieldHelpers + """
        uniform sampler2D uScratch;
        uniform sampler2D uPrev;
        uniform vec2 uRes;
        void main() {
            vec2 p = floor(gl_FragCoord.xy);
            float w = uRes.x, h = uRes.y;
            if (p.y == 0.0 || p.y == h - 1.0) {
                gl_FragColor = p.x == w - 1.0 ? vec4(fetch(uPrev, p, uRes), 1.0) : vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }
            vec3 c = fetchInt(uScratch, p, uRes);
            vec3 up = fetchInt(uScratch, p - vec2(0.0, 1.0), uRes);
            vec3 dn = fetchInt(uScratch, p + vec2(0.0, 1.0), uRes);
            vec3 le = p.x == 0.0 ? fetchInt(uScratch, vec2(w - 1.0, p.y - 1.0), uRes) : fetchInt(uScratch, p - vec2(1.0, 0.0), uRes);
            vec3 ri = p.x == w - 1.0 ? fetchInt(uScratch, vec2(0.0, p.y + 1.0), uRes) : fetchInt(uScratch, p + vec2(1.0, 0.0), uRes);
            vec3 avg = floor((up + dn + le + ri) / 4.0);
            gl_FragColor = vec4(floor((avg * 3.0 + c) / 4.0) / 255.0, 1.0);
        }
        """;

    private string FragmentHBlur() => Header() + FieldHelpers + $$"""
        uniform sampler2D uSrc;
        uniform vec2 uRes;
        uniform float uGauss[{{MaxBlurRadius + 1}}];
        uniform int uRadius;
        void main() {
            vec2 p = floor(gl_FragCoord.xy);
            vec3 sum = fetch(uSrc, p, uRes) * uGauss[0];
            for (int i = 1; i <= {{MaxBlurRadius}}; i++) {
                if (i > uRadius) break;
                float o = float(i);
                sum += (fetch(uSrc, vec2(max(p.x - o, 0.0), p.y), uRes)
                      + fetch(uSrc, vec2(min(p.x + o, uRes.x - 1.0), p.y), uRes)) * uGauss[i];
            }
            gl_FragColor = vec4(sum, 1.0);
        }
        """;

    /// <summary>
    /// The vertical half of the Gaussian, then the original's combine, written so it stays exact at a
    /// scale of 1 and keeps the FADE right at any other.
    ///
    /// The original's step is <c>floor((3·floor(S/4) + c)/4)</c>, where S sums the four neighbours. That
    /// is identically <c>(3·M + c)/4 − L</c>, where M = S/4 and the truncation loss
    /// <c>L = ¾·(S mod 4)/4 + ((3·floor(S/4) + c) mod 4)/4</c>. Nothing else fades the trails. L is zero
    /// where all five pixels agree and averages about ⅔ of a level where they differ, so it depends on
    /// how much the picture varies from one FIELD pixel to the next, and has to be estimated here:
    /// <list type="bullet">
    /// <item>From adjacent device pixels, which differ far less, it fades too slowly (<c>sweep-gpu</c>:
    /// median brightness 42 against the CPU's 37).</item>
    /// <item>From point samples one field unit away, it fades too fast (34). Those samples carry detail
    /// finer than a field pixel, which the original's coarser pixels never had.</item>
    /// <item>From samples one field unit away in the horizontally BLURRED intermediate, it matches
    /// (37.2 at a scale of 2, 36.8 at 3). This is the one used.</item>
    /// </list>
    /// </summary>
    private string FragmentVBlur() => Header() + FieldHelpers + $$"""
        uniform sampler2D uSrc;
        uniform sampler2D uScratch;
        uniform vec2 uRes;
        uniform float uGauss[{{MaxBlurRadius + 1}}];
        uniform int uRadius;
        uniform float uCentre;
        uniform float uEdge;
        uniform float uUnit;
        void main() {
            vec2 p = floor(gl_FragCoord.xy);
            if (p.y < uEdge || p.y >= uRes.y - uEdge) {
                gl_FragColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }
            vec3 g = fetch(uSrc, p, uRes) * uGauss[0];
            for (int i = 1; i <= {{MaxBlurRadius}}; i++) {
                if (i > uRadius) break;
                float o = float(i);
                g += (fetch(uSrc, vec2(p.x, max(p.y - o, 0.0)), uRes)
                    + fetch(uSrc, vec2(p.x, min(p.y + o, uRes.y - 1.0)), uRes)) * uGauss[i];
            }
            vec3 c = fetchInt(uScratch, p, uRes);
            vec3 m = (g * 255.0 - uCentre * c) / (1.0 - uCentre);

            vec2 hi = uRes - 1.0;
            vec3 s = fetchInt(uSrc, vec2(p.x, max(p.y - uUnit, 0.0)), uRes)
                   + fetchInt(uSrc, vec2(p.x, min(p.y + uUnit, hi.y)), uRes)
                   + fetchInt(uSrc, vec2(max(p.x - uUnit, 0.0), p.y), uRes)
                   + fetchInt(uSrc, vec2(min(p.x + uUnit, hi.x), p.y), uRes);
            vec3 avg = floor(s / 4.0);
            vec3 r1 = s - avg * 4.0;
            vec3 t = avg * 3.0 + c;
            vec3 r2 = t - floor(t / 4.0) * 4.0;
            vec3 loss = (r1 * 0.75 + r2) / 4.0;

            gl_FragColor = vec4(clamp(floor((m * 3.0 + c) / 4.0 - loss + 0.5), 0.0, 255.0) / 255.0, 1.0);
        }
        """;

    private string VertexStroke() => Header() + """
        attribute vec2 aPos;
        attribute float aAcross;
        attribute vec3 aColor;
        attribute vec3 aProfile;
        attribute float aClip;
        uniform float uScale;
        uniform vec2 uDevice;
        varying float vAcross;
        varying vec3 vColor;
        varying vec3 vProfile;
        varying float vClip;
        void main() {
            vAcross = aAcross;
            vColor = aColor;
            vProfile = aProfile;
            vClip = aClip;
            gl_Position = vec4(aPos * uScale / uDevice * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// A stroke band: the row before the line, the line, and the row after, each blended toward the
    /// colour by its <see cref="StrokeProfile"/> weight. Nothing is drawn inside the stroke margin.
    /// </summary>
    private string FragmentStroke() => Header() + """
        uniform float uScale;
        uniform vec2 uField;
        varying float vAcross;
        varying vec3 vColor;
        varying vec3 vProfile;
        varying float vClip;
        void main() {
            vec2 f = gl_FragCoord.xy / uScale;
            float lo = max(vClip, 2.0) - 1.0;
            if (f.x < lo || f.y < lo || f.x >= uField.x - lo || f.y >= uField.y - lo) discard;
            float a = vAcross < -0.5 ? vProfile.x : (vAcross > 0.5 ? vProfile.z : vProfile.y);
            if (a <= 0.0) discard;
            gl_FragColor = vec4(vColor, a);
        }
        """;

    private string VertexDisc() => Header() + """
        attribute vec2 aCorner;
        uniform float uScale;
        uniform vec2 uDevice;
        uniform vec4 uDisc;
        void main() {
            // The square the original scans: pixels cx-r .. cx+r on both axes.
            vec2 field = uDisc.xy - uDisc.z + aCorner * (2.0 * uDisc.z + 1.0);
            gl_Position = vec4(field * uScale / uDevice * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// <see cref="DrawPrimitives.Disc"/>, per device pixel, in field units. Pass 0 is the core and pass 1
    /// the rim. Both are lerps, which blending reproduces.
    /// </summary>
    private string FragmentDisc() => Header() + $$"""
        uniform float uScale;
        uniform vec4 uDisc;
        uniform vec3 uFill;
        uniform vec3 uRim;
        uniform float uPass;
        const float HALF_PI = 1.5707963267949;
        void main() {
            vec2 d = gl_FragCoord.xy / uScale - 0.5 - uDisc.xy;
            float r = uDisc.z;
            float d2 = (r * r - dot(d, d)) / r;
            if (uPass < 0.5) {
                if (d2 < 1.0) discard;
                float t = d2 / r;
                // The blend keeps cos(..)*alpha of what is already there.
                gl_FragColor = vec4(uFill, 1.0 - cos(t * t * HALF_PI) * uDisc.w);
            } else {
                if (!(d2 > -{{RimLimit}} && d2 < {{RimLimit}})) discard;
                gl_FragColor = vec4(uRim, (cos(abs(d2)) + 1.0) * 0.5);
            }
        }
        """;

    private string VertexCopy() => Header() + """
        attribute vec2 aPos;
        varying vec2 vUv;
        void main() { vUv = aPos * 0.5 + 0.5; gl_Position = vec4(aPos, 0.0, 1.0); }
        """;

    /// <summary>The whole texture across the whole viewport, optionally flipped so field row 0 is on top.</summary>
    private string FragmentCopy() => Header() + """
        uniform sampler2D uTex;
        uniform float uFlip;
        varying vec2 vUv;
        void main() {
            vec2 uv = vec2(vUv.x, uFlip > 0.5 ? 1.0 - vUv.y : vUv.y);
            gl_FragColor = vec4(texture2D(uTex, uv).rgb, 1.0);
        }
        """;
}
