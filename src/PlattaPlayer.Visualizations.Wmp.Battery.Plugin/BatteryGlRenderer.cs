using System;
using PlattaPlayer.Visualizations.Wmp.Battery.Gpu;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Plugin;

/// <summary>
/// The GPU half of the window-resolution Battery: the 8-bit field at device resolution, and the passes
/// that replay a <see cref="BatteryGpuFrame"/> on it. It knows nothing about Avalonia. It needs a current GL
/// context and a <see cref="GlBindings"/>, so the harness can run the very same passes offscreen and
/// check them against the CPU port.
///
/// The field is a palette INDEX per pixel, held in the red channel of two device-sized RGBA8 targets
/// that ping-pong as the original's two surfaces do. Row r of every texture is field row r, so row 0 (the
/// top of the picture) sits at the BOTTOM of the texture, and only <see cref="Present"/> flips. The
/// recorded steps replay in order:
/// <list type="bullet">
/// <item><b>Primitives</b> are drawn with no blending, so later ones overwrite earlier ones as in the
/// original.</item>
/// <item><b>Gather</b> copies each pixel from its warp source. At scale 1 the source is the field's own
/// map (the live table or a transition step) uploaded as it is, so it is exact by construction. At
/// other scales it is the <see cref="DeviceShiftTables"/> table. A transition is formed in the shader as
/// <c>old + trunc(f·(new − old))</c> per axis, and CLinearShift's fraction of a device pixel is carried as
/// a whole-pixel offset frame to frame.</item>
/// <item><b>Blur</b> is also the fade. At scale 1 it is the original's flat walk exactly:
/// <c>max(floor(sum5/5) − 1, 1)</c>, capped at 254, with its edge wrap and its two special-cased corners.
/// At other scales the plus kernel becomes a separable Gaussian with the same spread in field units
/// (variance 0.4·s² device px² per axis), and <see cref="BlurLoss"/> restores what the original's integer
/// division loses on average.</item>
/// </list>
/// Maps are packed RGBA8: x in R·256 + G, y in B·256 + A, read back exactly with nearest sampling.
/// </summary>
internal sealed class BatteryGlRenderer
{
    /// <summary>The widest Gaussian the blur shaders apply (taps either side).</summary>
    private const int MaxBlurRadius = 24;

    private readonly GlBindings _gl;
    private readonly bool _isGles;

    private int _primProgram, _gatherProgram, _exactBlurProgram, _hBlurProgram, _vBlurProgram, _presentProgram;

    private int _pPos, _pIndex, _pScale, _pDevice;
    private int _gPos, _gPrev, _gNew, _gOld, _gRes, _gTransition, _gOffset;
    private int _ePos, _eSrc, _eRes, _eMutate;
    private int _hPos, _hSrc, _hRes, _hWeights, _hRadius;
    private int _vPos, _vSrc, _vRes, _vWeights, _vRadius, _vLoss;
    private int _cPos, _cTex, _cPal, _cFlip, _cRaw;

    private int _quadVbo, _primVbo;

    private int _texA, _fboA, _texB, _fboB, _texT, _fboT;
    private int _mapNew, _mapOld, _palette;

    // What the map textures hold, so an unchanged table is not uploaded again.
    private object? _newKey, _oldKey;
    private int _newVersion = -1;
    private byte[] _mapBytes = [];

    // CLinearShift's carried fraction, per axis.
    private DeviceShiftTables.Entry? _scrollEntry;
    private double _scrollX, _scrollY;

    private readonly float[] _gauss = new float[MaxBlurRadius + 1];
    private int _gaussRadius;

    private readonly byte[] _paletteBytes = new byte[256 * 4];
    private bool _visible;
    private uint _stopFill;

    public BatteryGlRenderer(GlBindings gl, bool isGles)
    {
        _gl = gl;
        _isGles = isGles;
    }

    /// <summary>The current field geometry; default until <see cref="EnsureSize"/>.</summary>
    public BatteryFieldScale Scale { get; private set; }

    /// <summary>
    /// λ in the scaled blur's <c>floor(g + λ) − 1</c>. The original floors <c>sum/5</c>, which loses 0.4 of
    /// a level on average wherever the five taps differ. A floor of the Gaussian's real-valued mean
    /// loses 0.5, so λ ≈ 0.1 puts the difference back. Calibrated by <c>sweep-battery-gpu</c>. Not used at
    /// scale 1.
    /// </summary>
    public float BlurLoss { get; set; } = 0.1f;

    /// <summary>Harness mutation test: break the exact blur's corner special case.</summary>
    public bool MutateExactBlur { get; set; }

    public void Init()
    {
        var g = _gl;

        _primProgram = g.BuildProgram(VertexPrim(), FragmentPrim());
        _pPos = g.GetAttrib(_primProgram, "aPos");
        _pIndex = g.GetAttrib(_primProgram, "aIndex");
        _pScale = g.GetUniform(_primProgram, "uScale");
        _pDevice = g.GetUniform(_primProgram, "uDevice");

        _gatherProgram = g.BuildProgram(VertexQuad(), FragmentGather());
        _gPos = g.GetAttrib(_gatherProgram, "aPos");
        _gPrev = g.GetUniform(_gatherProgram, "uPrev");
        _gNew = g.GetUniform(_gatherProgram, "uNew");
        _gOld = g.GetUniform(_gatherProgram, "uOld");
        _gRes = g.GetUniform(_gatherProgram, "uRes");
        _gTransition = g.GetUniform(_gatherProgram, "uTransition");
        _gOffset = g.GetUniform(_gatherProgram, "uOffset");

        _exactBlurProgram = g.BuildProgram(VertexQuad(), FragmentExactBlur());
        _ePos = g.GetAttrib(_exactBlurProgram, "aPos");
        _eSrc = g.GetUniform(_exactBlurProgram, "uSrc");
        _eRes = g.GetUniform(_exactBlurProgram, "uRes");
        _eMutate = g.GetUniform(_exactBlurProgram, "uMutate");

        _hBlurProgram = g.BuildProgram(VertexQuad(), FragmentHBlur());
        _hPos = g.GetAttrib(_hBlurProgram, "aPos");
        _hSrc = g.GetUniform(_hBlurProgram, "uSrc");
        _hRes = g.GetUniform(_hBlurProgram, "uRes");
        _hWeights = UniformArray(_hBlurProgram, "uGauss");
        _hRadius = g.GetUniform(_hBlurProgram, "uRadius");

        _vBlurProgram = g.BuildProgram(VertexQuad(), FragmentVBlur());
        _vPos = g.GetAttrib(_vBlurProgram, "aPos");
        _vSrc = g.GetUniform(_vBlurProgram, "uSrc");
        _vRes = g.GetUniform(_vBlurProgram, "uRes");
        _vWeights = UniformArray(_vBlurProgram, "uGauss");
        _vRadius = g.GetUniform(_vBlurProgram, "uRadius");
        _vLoss = g.GetUniform(_vBlurProgram, "uLoss");

        _presentProgram = g.BuildProgram(VertexCopy(), FragmentPresent());
        _cPos = g.GetAttrib(_presentProgram, "aPos");
        _cTex = g.GetUniform(_presentProgram, "uTex");
        _cPal = g.GetUniform(_presentProgram, "uPal");
        _cFlip = g.GetUniform(_presentProgram, "uFlip");
        _cRaw = g.GetUniform(_presentProgram, "uRaw");

        _quadVbo = g.GenBuffer();
        g.BindBuffer(GlBindings.ArrayBuffer, _quadVbo);
        g.BufferData(GlBindings.ArrayBuffer, [-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f], GlBindings.StaticDraw);
        _primVbo = g.GenBuffer();

        _mapNew = CreateTexture();
        _mapOld = CreateTexture();
        _palette = CreateTexture();
        g.TexImageRgba(256, 1, _paletteBytes);
    }

    public void Dispose()
    {
        var g = _gl;
        foreach (var p in new[] { _primProgram, _gatherProgram, _exactBlurProgram, _hBlurProgram, _vBlurProgram, _presentProgram })
            if (p != 0) g.DeleteProgram(p);
        foreach (var b in new[] { _quadVbo, _primVbo })
            if (b != 0) g.DeleteBuffer(b);
        foreach (var t in new[] { _mapNew, _mapOld, _palette })
            if (t != 0) g.DeleteTexture(t);
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
    public void EnsureSize(BatteryFieldScale scale)
    {
        if (scale == Scale && _texA != 0) return;
        var w = scale.DeviceWidth;
        var h = scale.DeviceHeight;

        var (newA, newFboA) = CreateTarget(w, h);
        if (_texA != 0)
        {
            _gl.BindFramebuffer(newFboA);
            _gl.Viewport(0, 0, w, h);
            DrawPresent(_texA, flip: false, raw: true);
        }

        DeleteTargets();
        (_texA, _fboA) = (newA, newFboA);
        (_texB, _fboB) = CreateTarget(w, h);
        (_texT, _fboT) = CreateTarget(w, h);
        Scale = scale;
        _newKey = _oldKey = null;
        _scrollEntry = null;
        BuildGaussian(scale.Scale);
    }

    /// <summary>The scaled blur's kernel: a 1-D Gaussian whose discrete variance is the plus kernel's
    /// 0.4 field px², i.e. 0.4·s² device px².</summary>
    private void BuildGaussian(double s)
    {
        var target = 0.4 * s * s;
        double lo = 0.05, hi = Math.Max(1.0, s * 2);
        double[] weights = [];
        for (var iter = 0; iter < 60; iter++)
        {
            var sigma = (lo + hi) / 2;
            weights = Kernel(sigma, out var variance);
            if (variance < target) lo = sigma;
            else hi = sigma;
        }

        Array.Clear(_gauss);
        _gaussRadius = weights.Length - 1;
        for (var i = 0; i < weights.Length; i++) _gauss[i] = (float)weights[i];
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

    /// <summary>Replay one recorded frame onto the field.</summary>
    /// <param name="tables">The device tables; may be null at scale 1.</param>
    public void Step(BatteryGpuFrame frame, DeviceShiftTables? tables)
    {
        var g = _gl;
        var scale = Scale;
        var w = scale.DeviceWidth;
        var h = scale.DeviceHeight;
        g.Disable(GlBindings.Blend);

        for (var i = 0; i < 256; i++)
        {
            var e = frame.Palette[i];
            _paletteBytes[i * 4] = (byte)e;
            _paletteBytes[i * 4 + 1] = (byte)(e >> 8);
            _paletteBytes[i * 4 + 2] = (byte)(e >> 16);
            _paletteBytes[i * 4 + 3] = 255;
        }
        g.ActiveTexture(GlBindings.Texture0);
        g.BindTexture(GlBindings.Texture2D, _palette);
        g.TexImageRgba(256, 1, _paletteBytes);
        _visible = frame.Visible;
        _stopFill = frame.StopFill;

        var uploaded = false;
        foreach (var cmd in frame.Commands)
        {
            switch (cmd.Op)
            {
                case BatteryGpuOp.Prims:
                    if (!uploaded)
                    {
                        g.BindBuffer(GlBindings.ArrayBuffer, _primVbo);
                        g.BufferDataRange(GlBindings.ArrayBuffer, frame.Vertices,
                                          frame.VertexCount * BatteryGpuFrame.FloatsPerVertex, GlBindings.DynamicDraw);
                        uploaded = true;
                    }
                    g.BindFramebuffer(_fboA);
                    g.Viewport(0, 0, w, h);
                    UseProgram(_primProgram);
                    g.BindBuffer(GlBindings.ArrayBuffer, _primVbo);
                    Attrib(_pPos, 2, BatteryGpuFrame.FloatsPerVertex, 0);
                    Attrib(_pIndex, 1, BatteryGpuFrame.FloatsPerVertex, 2);
                    g.Uniform1f(_pScale, (float)scale.Scale);
                    g.Uniform2f(_pDevice, w, h);
                    g.DrawArrays(GlBindings.Triangles, cmd.First, cmd.Count);
                    break;

                case BatteryGpuOp.Gather:
                    Gather(cmd, tables);
                    break;

                case BatteryGpuOp.Blur:
                    Blur();
                    break;
            }
        }
    }

    private void Gather(in BatteryGpuCommand cmd, DeviceShiftTables? tables)
    {
        var g = _gl;
        var scale = Scale;
        int w = scale.DeviceWidth, h = scale.DeviceHeight;
        var transition = -1f;
        float offX = 0, offY = 0;

        if (scale.IsExact || tables is null)
        {
            // The field's own map, exact by construction (transition steps included).
            if (!ReferenceEquals(_newKey, cmd.Map) || _newVersion != cmd.Version)
            {
                UploadFieldMap(cmd.Map!, w, h);
                _newKey = cmd.Map;
                _newVersion = cmd.Version;
            }
        }
        else
        {
            var e = tables.Resolve(cmd.Table!, cmd.Version, withOld: cmd.Step >= 0);
            if (!ReferenceEquals(_newKey, e))
            {
                Upload(_mapNew, e.Rgba, w, h);
                _newKey = e;
                _newVersion = -1;
            }

            if (cmd.Step >= 0 && e.Old is { } old)
            {
                if (!ReferenceEquals(_oldKey, old))
                {
                    Upload(_mapOld, old.Rgba, w, h);
                    _oldKey = old;
                }
                transition = (float)cmd.TransitionFactor;
            }
            else if (cmd.Step < 0 && e.ScrollRemainder != (0.0, 0.0))
            {
                // A scroll of a fractional number of device pixels a frame: the table moves the whole
                // part, and the fraction accumulates here until it makes another pixel.
                if (!ReferenceEquals(_scrollEntry, e))
                {
                    _scrollEntry = e;
                    _scrollX = _scrollY = 0;
                }
                _scrollX += e.ScrollRemainder.X;
                _scrollY += e.ScrollRemainder.Y;
                offX = (float)Math.Truncate(_scrollX);
                offY = (float)Math.Truncate(_scrollY);
                _scrollX -= offX;
                _scrollY -= offY;
            }
        }

        g.BindFramebuffer(_fboB);
        g.Viewport(0, 0, w, h);
        UseProgram(_gatherProgram);
        BindQuad(_gPos);
        BindTexture(0, _texA, _gPrev);
        BindTexture(1, _mapNew, _gNew);
        BindTexture(2, _mapOld, _gOld);
        g.Uniform2f(_gRes, w, h);
        g.Uniform1f(_gTransition, transition);
        g.Uniform2f(_gOffset, offX, offY);
        g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
        SwapField();
    }

    private void Blur()
    {
        var g = _gl;
        var scale = Scale;
        int w = scale.DeviceWidth, h = scale.DeviceHeight;
        if (scale.IsExact)
        {
            g.BindFramebuffer(_fboB);
            g.Viewport(0, 0, w, h);
            UseProgram(_exactBlurProgram);
            BindQuad(_ePos);
            BindTexture(0, _texA, _eSrc);
            g.Uniform2f(_eRes, w, h);
            g.Uniform1f(_eMutate, MutateExactBlur ? 1f : 0f);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
        }
        else
        {
            g.BindFramebuffer(_fboT);
            g.Viewport(0, 0, w, h);
            UseProgram(_hBlurProgram);
            BindQuad(_hPos);
            BindTexture(0, _texA, _hSrc);
            g.Uniform2f(_hRes, w, h);
            g.Uniform1fv(_hWeights, _gauss);
            g.Uniform1i(_hRadius, _gaussRadius);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);

            g.BindFramebuffer(_fboB);
            UseProgram(_vBlurProgram);
            BindQuad(_vPos);
            BindTexture(0, _texT, _vSrc);
            g.Uniform2f(_vRes, w, h);
            g.Uniform1fv(_vWeights, _gauss);
            g.Uniform1i(_vRadius, _gaussRadius);
            g.Uniform1f(_vLoss, BlurLoss);
            g.DrawArrays(GlBindings.TriangleStrip, 0, 4);
        }
        SwapField();
    }

    private void SwapField()
    {
        (_texA, _texB) = (_texB, _texA);
        (_fboA, _fboB) = (_fboB, _fboA);
    }

    /// <summary>Pack a field map (flat source indices) as x, y pairs.</summary>
    private void UploadFieldMap(int[] map, int w, int h)
    {
        if (_mapBytes.Length != w * h * 4) _mapBytes = new byte[w * h * 4];
        var b = _mapBytes;
        for (var i = 0; i < w * h; i++)
        {
            var src = (uint)map[i];
            var x = (int)(src % (uint)w);
            var y = (int)(src / (uint)w);
            b[i * 4] = (byte)(x >> 8);
            b[i * 4 + 1] = (byte)x;
            b[i * 4 + 2] = (byte)(y >> 8);
            b[i * 4 + 3] = (byte)y;
        }
        Upload(_mapNew, b, w, h);
    }

    private void Upload(int texture, byte[] rgba, int w, int h)
    {
        _gl.ActiveTexture(GlBindings.Texture0);
        _gl.BindTexture(GlBindings.Texture2D, texture);
        _gl.TexImageRgba(w, h, rgba);
    }

    /// <summary>Draw the latest frame to <paramref name="fb"/> through its palette, top row up; or, once
    /// the stop fade is over, the original's solid fill.</summary>
    public void Present(int fb, int width, int height) => Present(fb, width, height, flip: true);

    private void Present(int fb, int width, int height, bool flip)
    {
        _gl.BindFramebuffer(fb);
        _gl.Viewport(0, 0, width, height);
        _gl.Disable(GlBindings.Blend);
        if (!_visible)
        {
            var c = _stopFill;
            _gl.ClearColor((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1f);
            _gl.Clear(GlBindings.ColorBufferBit);
            return;
        }
        DrawPresent(_texA, flip, raw: false);
    }

    /// <summary>Erase the field to index 0 (what AllocateSurfaces does).</summary>
    public void Clear()
    {
        foreach (var fbo in new[] { _fboA, _fboB })
        {
            _gl.BindFramebuffer(fbo);
            _gl.Viewport(0, 0, Scale.DeviceWidth, Scale.DeviceHeight);
            _gl.ClearColor(0, 0, 0, 1);
            _gl.Clear(GlBindings.ColorBufferBit);
        }
        _newKey = _oldKey = null;
        _scrollEntry = null;
    }

    /// <summary>Harness: the latest field as RGBA, the index in R, field row 0 first.</summary>
    public void ReadField(byte[] rgba)
    {
        _gl.BindFramebuffer(_fboA);
        _gl.ReadPixelsRgba(0, 0, Scale.DeviceWidth, Scale.DeviceHeight, rgba);
    }

    /// <summary>Harness: what <see cref="Present"/> shows, as RGBA, field row 0 first. Uses the blur's
    /// scratch target.</summary>
    public void ReadPresented(byte[] rgba)
    {
        Present(_fboT, Scale.DeviceWidth, Scale.DeviceHeight, flip: false);
        _gl.ReadPixelsRgba(0, 0, Scale.DeviceWidth, Scale.DeviceHeight, rgba);
    }

    /// <summary>Harness: replace the latest field with <paramref name="indices"/>, field row 0 first.</summary>
    public void UploadField(byte[] indices)
    {
        var rgba = new byte[indices.Length * 4];
        for (var i = 0; i < indices.Length; i++)
        {
            rgba[i * 4] = indices[i];
            rgba[i * 4 + 3] = 255;
        }
        Upload(_texA, rgba, Scale.DeviceWidth, Scale.DeviceHeight);
    }

    private void DrawPresent(int texture, bool flip, bool raw)
    {
        UseProgram(_presentProgram);
        BindQuad(_cPos);
        BindTexture(0, texture, _cTex);
        BindTexture(1, _palette, _cPal);
        _gl.Uniform1f(_cFlip, flip ? 1f : 0f);
        _gl.Uniform1f(_cRaw, raw ? 1f : 0f);
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

    /// <summary>A texture with nearest sampling and clamped edges, bound to unit 0.</summary>
    private int CreateTexture()
    {
        var g = _gl;
        var tex = g.GenTexture();
        g.ActiveTexture(GlBindings.Texture0);
        g.BindTexture(GlBindings.Texture2D, tex);
        g.TexParameter(GlBindings.TextureMinFilter, GlBindings.Nearest);
        g.TexParameter(GlBindings.TextureMagFilter, GlBindings.Nearest);
        g.TexParameter(GlBindings.TextureWrapS, GlBindings.ClampToEdge);
        g.TexParameter(GlBindings.TextureWrapT, GlBindings.ClampToEdge);
        return tex;
    }

    private (int Texture, int Fbo) CreateTarget(int w, int h)
    {
        var g = _gl;
        var tex = CreateTexture();
        g.TexImageEmpty(w, h);
        var fbo = g.GenFramebuffer();
        g.BindFramebuffer(fbo);
        g.AttachColorTexture(tex);
        g.ClearColor(0, 0, 0, 1);
        g.Clear(GlBindings.ColorBufferBit);
        return (tex, fbo);
    }

    private void DeleteTargets()
    {
        foreach (var t in new[] { _texA, _texB, _texT })
            if (t != 0) _gl.DeleteTexture(t);
        foreach (var f in new[] { _fboA, _fboB, _fboT })
            if (f != 0) _gl.DeleteFramebuffer(f);
        _texA = _texB = _texT = 0;
        _fboA = _fboB = _fboT = 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Shaders. GLSL 1.20 / GLSL ES 1.00, so they run on desktop GL and on ANGLE alike. Every pass
    // works in device pixels addressed by gl_FragCoord, and integers are carried in floats (exact up to
    // 2^24, far beyond any window).

    private string Header() => _isGles ? "#version 100\nprecision highp float;\n" : "#version 120\n";

    private string VertexQuad() => Header() + """
        attribute vec2 aPos;
        void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
        """;

    /// <summary>Whole-pixel reads of a palette index (R) and of a packed map.</summary>
    private const string FieldHelpers = """
        float fieldIndex(sampler2D tex, vec2 px, vec2 res) { return floor(texture2D(tex, (px + 0.5) / res).r * 255.0 + 0.5); }
        vec2 source(sampler2D tex, vec2 px, vec2 res) {
            vec4 b = floor(texture2D(tex, (px + 0.5) / res) * 255.0 + 0.5);
            return vec2(b.r * 256.0 + b.g, b.b * 256.0 + b.a);
        }
        // (int) casts truncate toward zero; floor() differs for negatives.
        float trunc1(float v) { return v < 0.0 ? -floor(-v) : floor(v); }
        """;

    private string VertexPrim() => Header() + """
        attribute vec2 aPos;
        attribute float aIndex;
        uniform float uScale;
        uniform vec2 uDevice;
        varying float vIndex;
        void main() {
            vIndex = aIndex;
            gl_Position = vec4(aPos * uScale / uDevice * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private string FragmentPrim() => Header() + """
        varying float vIndex;
        void main() { gl_FragColor = vec4(floor(vIndex + 0.5) / 255.0, 0.0, 0.0, 1.0); }
        """;

    /// <summary>
    /// <c>dst = src[map]</c>. During a scaled transition the map is <c>old + trunc(f·(new − old))</c> per axis,
    /// as SetPixel forms its intermediate tables. A Linear scroll's carried pixel is added where it stays in
    /// the field.
    /// </summary>
    private string FragmentGather() => Header() + FieldHelpers + """
        uniform sampler2D uPrev;
        uniform sampler2D uNew;
        uniform sampler2D uOld;
        uniform vec2 uRes;
        // The transition's factor, or negative for none.
        uniform float uTransition;
        uniform vec2 uOffset;
        void main() {
            vec2 dest = floor(gl_FragCoord.xy);
            vec2 src = source(uNew, dest, uRes);
            if (uTransition >= 0.0) {
                vec2 old = source(uOld, dest, uRes);
                vec2 d = src - old;
                src = old + vec2(trunc1(d.x * uTransition), trunc1(d.y * uTransition));
            }
            vec2 moved = src + uOffset;
            if (moved.x >= 0.0 && moved.x < uRes.x) src.x = moved.x;
            if (moved.y >= 0.0 && moved.y < uRes.y) src.y = moved.y;
            gl_FragColor = vec4(fieldIndex(uPrev, src, uRes) / 255.0, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// <see cref="PlusBlur.Perform"/> exactly. The walk is flat, so a row's first pixel takes its left
    /// neighbour from the end of the row above and its last pixel its right neighbour from the start of
    /// the row below; up and down wrap top to bottom; and the two corners the code special-cases, (0,0)
    /// and (w−1,h−1), take that neighbour from their own row instead.
    /// </summary>
    private string FragmentExactBlur() => Header() + FieldHelpers + """
        uniform sampler2D uSrc;
        uniform vec2 uRes;
        uniform float uMutate;
        void main() {
            vec2 p = floor(gl_FragCoord.xy);
            float w = uRes.x, h = uRes.y;
            vec2 l = p.x > 0.0 ? vec2(p.x - 1.0, p.y)
                   : vec2(w - 1.0, (p.y == 0.0 && uMutate < 0.5) ? 0.0 : (p.y == 0.0 ? h - 1.0 : p.y - 1.0));
            vec2 r = p.x < w - 1.0 ? vec2(p.x + 1.0, p.y) : vec2(0.0, p.y == h - 1.0 ? h - 1.0 : p.y + 1.0);
            vec2 u = vec2(p.x, p.y == 0.0 ? h - 1.0 : p.y - 1.0);
            vec2 d = vec2(p.x, p.y == h - 1.0 ? 0.0 : p.y + 1.0);
            float s = fieldIndex(uSrc, p, uRes) + fieldIndex(uSrc, l, uRes) + fieldIndex(uSrc, r, uRes)
                    + fieldIndex(uSrc, u, uRes) + fieldIndex(uSrc, d, uRes);
            // floor(s / 5) for whole s, safe against an inexact division.
            float q = floor((s + 0.5) / 5.0);
            gl_FragColor = vec4(clamp(q - 1.0, 1.0, 254.0) / 255.0, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>The horizontal half of the scaled blur, wrapping round the sides. The result keeps 1/256 of
    /// a level: the whole part in R and the fraction in G.</summary>
    private string FragmentHBlur() => Header() + FieldHelpers + $$"""
        uniform sampler2D uSrc;
        uniform vec2 uRes;
        uniform float uGauss[{{MaxBlurRadius + 1}}];
        uniform int uRadius;
        float wrapX(float x) { return x < 0.0 ? x + uRes.x : (x >= uRes.x ? x - uRes.x : x); }
        void main() {
            vec2 p = floor(gl_FragCoord.xy);
            float sum = fieldIndex(uSrc, p, uRes) * uGauss[0];
            for (int i = 1; i <= {{MaxBlurRadius}}; i++) {
                if (i > uRadius) break;
                float o = float(i);
                sum += (fieldIndex(uSrc, vec2(wrapX(p.x - o), p.y), uRes)
                      + fieldIndex(uSrc, vec2(wrapX(p.x + o), p.y), uRes)) * uGauss[i];
            }
            float whole = floor(sum);
            float frac = floor((sum - whole) * 256.0 + 0.5);
            if (frac >= 256.0) { whole += 1.0; frac = 0.0; }
            gl_FragColor = vec4(whole / 255.0, frac / 255.0, 0.0, 1.0);
        }
        """;

    /// <summary>The vertical half, wrapping top to bottom, then the original's fade:
    /// <c>max(floor(g + λ) − 1, 1)</c>, capped at 254.</summary>
    private string FragmentVBlur() => Header() + $$"""
        uniform sampler2D uSrc;
        uniform vec2 uRes;
        uniform float uGauss[{{MaxBlurRadius + 1}}];
        uniform int uRadius;
        uniform float uLoss;
        float wrapY(float y) { return y < 0.0 ? y + uRes.y : (y >= uRes.y ? y - uRes.y : y); }
        float level(vec2 px) {
            vec2 b = floor(texture2D(uSrc, (px + 0.5) / uRes).rg * 255.0 + 0.5);
            return b.x + b.y / 256.0;
        }
        void main() {
            vec2 p = floor(gl_FragCoord.xy);
            float g = level(p) * uGauss[0];
            for (int i = 1; i <= {{MaxBlurRadius}}; i++) {
                if (i > uRadius) break;
                float o = float(i);
                g += (level(vec2(p.x, wrapY(p.y - o))) + level(vec2(p.x, wrapY(p.y + o)))) * uGauss[i];
            }
            gl_FragColor = vec4(clamp(floor(g + uLoss) - 1.0, 1.0, 254.0) / 255.0, 0.0, 0.0, 1.0);
        }
        """;

    private string VertexCopy() => Header() + """
        attribute vec2 aPos;
        varying vec2 vUv;
        void main() { vUv = aPos * 0.5 + 0.5; gl_Position = vec4(aPos, 0.0, 1.0); }
        """;

    /// <summary>The whole field across the whole viewport through the 256-entry palette, optionally flipped
    /// so field row 0 is on top. <c>uRaw</c> copies the index itself (resizing the field).</summary>
    private string FragmentPresent() => Header() + """
        uniform sampler2D uTex;
        uniform sampler2D uPal;
        uniform float uFlip;
        uniform float uRaw;
        varying vec2 vUv;
        void main() {
            vec2 uv = vec2(vUv.x, uFlip > 0.5 ? 1.0 - vUv.y : vUv.y);
            float i = floor(texture2D(uTex, uv).r * 255.0 + 0.5);
            if (uRaw > 0.5) { gl_FragColor = vec4(i / 255.0, 0.0, 0.0, 1.0); return; }
            gl_FragColor = vec4(texture2D(uPal, vec2((i + 0.5) / 256.0, 0.5)).rgb, 1.0);
        }
        """;
}
