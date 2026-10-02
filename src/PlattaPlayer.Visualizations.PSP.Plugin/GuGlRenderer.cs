using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PlattaPlayer.Visualizations.PSP.Gu;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Plugin;

/// <summary>
/// Replays a recorded <see cref="GuFrame"/> on OpenGL (ES) 2.0. The engine has already decoded and
/// transformed every vertex and resolved its colour and texture coordinates, so what is left here is the
/// GE's per-fragment state: the texture function (one shader), the alpha test, and blending — with GE
/// factor semantics, mapped as the decompilation's GL host does.
///
/// The 480x272 virtual screen is drawn straight into the target framebuffer at its full resolution, so the
/// picture is stretched to the control (PSP screens are 30:17, close enough to 16:9 that typical windows
/// barely distort it). The framebuffer's alpha is kept at 1: on the PSP it is the stencil plane and
/// nothing here should make the control translucent.
/// </summary>
internal sealed class GuGlRenderer : IDisposable
{
    private const int GlZero = 0, GlOne = 1;
    private const int GlSrcColor = 0x300, GlOneMinusSrcColor = 0x301, GlSrcAlpha = 0x302, GlOneMinusSrcAlpha = 0x303;
    private const int GlDstAlpha = 0x304, GlOneMinusDstAlpha = 0x305, GlDstColor = 0x306, GlOneMinusDstColor = 0x307;
    private const int GlConstantColor = 0x8001;
    private const int GlFuncAdd = 0x8006, GlMin = 0x8007, GlMax = 0x8008, GlFuncSubtract = 0x800A, GlFuncReverseSubtract = 0x800B;

    private static readonly int Stride = Unsafe.SizeOf<GuVertex>();

    private readonly GlBindings _gl;
    private readonly bool _isGles;
    private readonly Dictionary<int, (int Name, int Version)> _textures = new();
    private int _program;
    private int _buffer;
    private int _uTex, _uTextured, _uTfx, _uTcc, _uAlphaTest, _uAlphaFunc, _uAlphaRef;

    public GuGlRenderer(GlBindings gl, bool isGles)
    {
        _gl = gl;
        _isGles = isGles;
    }

    public void Init()
    {
        var header = _isGles ? "#version 100\nprecision highp float;\nprecision highp int;\n" : "#version 120\n";
        const string vertex = """
            attribute vec4 aPos;
            attribute vec2 aUv;
            attribute vec4 aColor;
            varying vec2 vUv;
            varying vec4 vColor;
            void main() {
                gl_Position = aPos;
                vUv = aUv;
                vColor = aColor;
            }
            """;
        // GU texture functions (GE semantics; the BLEND environment colour is left at its reset value, black):
        //   MODULATE c*t   DECAL mix(c, t, t.a) / t   BLEND mix(c, env, t)   REPLACE t   ADD c+t
        // TCC_RGB ignores the texture's alpha. The alpha test compares 8-bit values like the GE.
        const string fragment = """
            uniform sampler2D uTex;
            uniform int uTextured;
            uniform int uTfx;
            uniform int uTcc;
            uniform int uAlphaTest;
            uniform int uAlphaFunc;
            uniform float uAlphaRef;
            varying vec2 vUv;
            varying vec4 vColor;
            void main() {
                vec4 c = vColor;
                if (uTextured == 1) {
                    vec4 t = texture2D(uTex, vUv);
                    vec3 rgb;
                    float a = c.a;
                    if (uTfx == 1) {
                        rgb = uTcc == 1 ? mix(c.rgb, t.rgb, t.a) : t.rgb;
                    } else {
                        if (uTfx == 0) rgb = c.rgb * t.rgb;
                        else if (uTfx == 2) rgb = mix(c.rgb, vec3(0.0), t.rgb);
                        else if (uTfx == 3) rgb = t.rgb;
                        else rgb = min(c.rgb + t.rgb, vec3(1.0));
                        if (uTcc == 1) a = uTfx == 3 ? t.a : c.a * t.a;
                    }
                    c = vec4(rgb, a);
                }
                if (uAlphaTest == 1) {
                    float a8 = floor(c.a * 255.0 + 0.5);
                    bool pass = true;
                    if (uAlphaFunc == 0) pass = false;
                    else if (uAlphaFunc == 2) pass = a8 == uAlphaRef;
                    else if (uAlphaFunc == 3) pass = a8 != uAlphaRef;
                    else if (uAlphaFunc == 4) pass = a8 < uAlphaRef;
                    else if (uAlphaFunc == 5) pass = a8 <= uAlphaRef;
                    else if (uAlphaFunc == 6) pass = a8 > uAlphaRef;
                    else if (uAlphaFunc == 7) pass = a8 >= uAlphaRef;
                    if (!pass) discard;
                }
                gl_FragColor = c;
            }
            """;
        _program = _gl.BuildProgram(header + vertex, header + fragment, "aPos", "aUv", "aColor");
        _uTex = _gl.GetUniform(_program, "uTex");
        _uTextured = _gl.GetUniform(_program, "uTextured");
        _uTfx = _gl.GetUniform(_program, "uTfx");
        _uTcc = _gl.GetUniform(_program, "uTcc");
        _uAlphaTest = _gl.GetUniform(_program, "uAlphaTest");
        _uAlphaFunc = _gl.GetUniform(_program, "uAlphaFunc");
        _uAlphaRef = _gl.GetUniform(_program, "uAlphaRef");
        _buffer = _gl.GenBuffer();
    }

    /// <summary>Clears <paramref name="fb"/> to black and replays <paramref name="frame"/> into it.</summary>
    public void Render(GuFrame frame, int fb, int width, int height)
    {
        _gl.BindFramebuffer(fb);
        _gl.Viewport(0, 0, width, height);
        _gl.Disable(GlBindings.DepthTest);
        _gl.Disable(GlBindings.CullFace);
        _gl.Disable(GlBindings.ScissorTest);
        _gl.ColorMask(true, true, true, true);
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(GlBindings.ColorBufferBit);
        _gl.ColorMask(true, true, true, false);

        _gl.UseProgram(_program);
        _gl.Uniform1i(_uTex, 0);
        _gl.ActiveTexture(GlBindings.Texture0);
        _gl.BindBuffer(_buffer);
        unsafe
        {
            fixed (GuVertex* p = frame.Vertices)
                _gl.BufferData(frame.VertexCount * Stride, (IntPtr)p);
        }
        _gl.VertexAttrib(0, 4, GlBindings.Float, false, Stride, 0);
        _gl.VertexAttrib(1, 2, GlBindings.Float, false, Stride, 16);
        _gl.VertexAttrib(2, 4, GlBindings.UnsignedByte, true, Stride, 24);
        for (var i = 0; i < 3; i++) _gl.EnableVertexAttrib(i);

        foreach (var command in frame.Commands)
        {
            if (command.Kind == GuCommandKind.Clear)
            {
                if ((command.ClearFlags & GU_COLOR_BUFFER_BIT) == 0) continue;
                var c = command.ClearColor;
                _gl.ClearColor((c & 0xff) / 255f, (c >> 8 & 0xff) / 255f, (c >> 16 & 0xff) / 255f, 1f);
                _gl.Clear(GlBindings.ColorBufferBit);
                continue;
            }
            Draw(command);
        }

        for (var i = 0; i < 3; i++) _gl.DisableVertexAttrib(i);
        _gl.BindBuffer(0);
        _gl.Disable(GlBindings.Blend);
        _gl.ColorMask(true, true, true, true);
        _gl.UseProgram(0);
    }

    /// <summary>Drops every cached GPU texture (after a visualizer switch, whose textures are gone with it).</summary>
    public void ResetTextures()
    {
        foreach (var (name, _) in _textures.Values) _gl.DeleteTexture(name);
        _textures.Clear();
    }

    public void Dispose()
    {
        ResetTextures();
        if (_buffer != 0) _gl.DeleteBuffer(_buffer);
        if (_program != 0) _gl.DeleteProgram(_program);
        _buffer = _program = 0;
    }

    private void Draw(in GuCommand command)
    {
        var state = command.State;
        ApplyBlend(state);
        var textured = state.Textured && command.Texture is not null;
        if (textured) BindTexture(command.Texture!);
        _gl.Uniform1i(_uTextured, textured ? 1 : 0);
        _gl.Uniform1i(_uTfx, state.TexFunc);
        _gl.Uniform1i(_uTcc, state.TexColorComponent);
        _gl.Uniform1i(_uAlphaTest, state.AlphaTest ? 1 : 0);
        _gl.Uniform1i(_uAlphaFunc, state.AlphaFunc);
        _gl.Uniform1f(_uAlphaRef, state.AlphaRef);

        var mode = command.Primitive switch
        {
            GuPrimitive.Points => 0,
            GuPrimitive.Lines => 1,
            GuPrimitive.LineStrip => 3,
            GuPrimitive.Triangles => 4,
            GuPrimitive.TriangleStrip => 5,
            _ => 6,
        };
        _gl.DrawArrays(mode, command.First, command.Count);
    }

    private void ApplyBlend(in GuDrawState s)
    {
        if (!s.Blend)
        {
            _gl.Disable(GlBindings.Blend);
            return;
        }
        _gl.Enable(GlBindings.Blend);
        _gl.BlendEquation(s.BlendOp switch
        {
            GU_SUBTRACT => GlFuncSubtract,
            GU_REVERSE_SUBTRACT => GlFuncReverseSubtract,
            GU_MIN => GlMin,
            GU_MAX => GlMax,
            _ => GlFuncAdd, // GU_ADD, and GU_ABS which GL has no equivalent for
        });
        var src = SourceFactor(s.BlendSrc, s.BlendSrcFix);
        var dst = DestFactor(s.BlendDst, s.BlendDstFix);
        if (src == GlConstantColor || dst == GlConstantColor)
        {
            var fix = src == GlConstantColor ? s.BlendSrcFix : s.BlendDstFix;
            _gl.BlendColor((fix & 0xff) / 255f, (fix >> 8 & 0xff) / 255f, (fix >> 16 & 0xff) / 255f, 1f);
        }
        _gl.BlendFunc(src, dst);
    }

    // GE: source factor 0/1 are DST_COLOR / 1-DST_COLOR; 10 is the fixed colour.
    private static int SourceFactor(int f, uint fix) => f switch
    {
        0 => GlDstColor,
        1 => GlOneMinusDstColor,
        2 or 6 => GlSrcAlpha,
        3 or 7 => GlOneMinusSrcAlpha,
        4 or 8 => GlDstAlpha,
        5 or 9 => GlOneMinusDstAlpha,
        _ => (fix & 0xffffff) == 0xffffff ? GlOne : (fix & 0xffffff) == 0 ? GlZero : GlConstantColor,
    };

    // GE: destination factor 0/1 are SRC_COLOR / 1-SRC_COLOR.
    private static int DestFactor(int f, uint fix) => f switch
    {
        0 => GlSrcColor,
        1 => GlOneMinusSrcColor,
        _ => SourceFactor(f, fix),
    };

    private void BindTexture(GuTexture texture)
    {
        if (_textures.TryGetValue(texture.Id, out var entry) && entry.Version == texture.Version)
        {
            _gl.BindTexture(entry.Name);
            return;
        }

        var name = entry.Name != 0 ? entry.Name : _gl.GenTexture();
        _gl.BindTexture(name);
        _gl.TexParameter(GlBindings.TextureMinFilter, GlBindings.Linear);
        _gl.TexParameter(GlBindings.TextureMagFilter, GlBindings.Linear);
        // GLES 2.0 only allows REPEAT on power-of-two textures; the GE's default wrap is repeat.
        var wrap = IsPowerOfTwo(texture.Width) && IsPowerOfTwo(texture.Height) ? GlBindings.Repeat : GlBindings.ClampToEdge;
        _gl.TexParameter(GlBindings.TextureWrapS, wrap);
        _gl.TexParameter(GlBindings.TextureWrapT, wrap);
        unsafe
        {
            // 0xAABBGGRR little-endian is bytes R, G, B, A: GL_RGBA as is. Row 0 (v = 0) first.
            fixed (uint* p = texture.Pixels)
                _gl.TexImageRgba(texture.Width, texture.Height, (IntPtr)p);
        }
        _textures[texture.Id] = (name, texture.Version);
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;
}
