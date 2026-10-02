using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.OpenGL;

namespace PlattaPlayer.Visualizations.PSP.Plugin;

/// <summary>
/// A thin, version-independent binding to the OpenGL (ES) 2.0 entry points the GU replay needs, resolved
/// through Avalonia's <see cref="GlInterface.GetProcAddress"/> (the same approach as the WMP plugins'
/// bindings, which are internal to their assemblies). Works on both the desktop-GL and ANGLE-GLES backends
/// Avalonia may select.
/// </summary>
internal sealed class GlBindings
{
    public const int ColorBufferBit = 0x4000;
    public const int Texture2D = 0x0DE1;
    public const int Rgba = 0x1908;
    public const int UnsignedByte = 0x1401;
    public const int Float = 0x1406;
    public const int TextureMagFilter = 0x2800;
    public const int TextureMinFilter = 0x2801;
    public const int TextureWrapS = 0x2802;
    public const int TextureWrapT = 0x2803;
    public const int Linear = 0x2601;
    public const int Repeat = 0x2901;
    public const int ClampToEdge = 0x812F;
    public const int Framebuffer = 0x8D40;
    public const int ArrayBuffer = 0x8892;
    public const int StreamDraw = 0x88E0;
    public const int FragmentShader = 0x8B30;
    public const int VertexShader = 0x8B31;
    public const int CompileStatus = 0x8B81;
    public const int LinkStatus = 0x8B82;
    public const int Blend = 0x0BE2;
    public const int DepthTest = 0x0B71;
    public const int CullFace = 0x0B44;
    public const int ScissorTest = 0x0C11;
    public const int Texture0 = 0x84C0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlViewport(int x, int y, int w, int h);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlClearColor(float r, float g, float b, float a);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlClear(int mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlEnable(int cap);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDisable(int cap);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBlendFunc(int s, int d);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBlendEquation(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBlendColor(float r, float g, float b, float a);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlColorMask(byte r, byte g, byte b, byte a);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGenTextures(int n, int[] textures);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBindTexture(int target, int texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlTexImage2D(int target, int level, int internalFormat, int w, int h, int border, int format, int type, IntPtr pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlTexParameteri(int target, int pname, int param);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlActiveTexture(int texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteTextures(int n, int[] textures);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBindFramebuffer(int target, int framebuffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGenBuffers(int n, int[] buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBindBuffer(int target, int buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBufferData(int target, IntPtr size, IntPtr data, int usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteBuffers(int n, int[] buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GlCreateShader(int type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlShaderSource(int shader, int count, string[] strings, int[]? lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlCompileShader(int shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetShaderiv(int shader, int pname, int[] @params);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetShaderInfoLog(int shader, int maxLength, out int length, StringBuilder infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteShader(int shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GlCreateProgram();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlAttachShader(int program, int shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBindAttribLocation(int program, int index, string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlLinkProgram(int program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetProgramiv(int program, int pname, int[] @params);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUseProgram(int program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteProgram(int program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GlGetUniformLocation(int program, string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform1i(int location, int v0);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform1f(int location, float v0);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlVertexAttribPointer(int index, int size, int type, byte normalized, int stride, IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlEnableVertexAttribArray(int index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDisableVertexAttribArray(int index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDrawArrays(int mode, int first, int count);

    private readonly GlViewport _viewport;
    private readonly GlClearColor _clearColor;
    private readonly GlClear _clear;
    private readonly GlEnable _enable;
    private readonly GlDisable _disable;
    private readonly GlBlendFunc _blendFunc;
    private readonly GlBlendEquation _blendEquation;
    private readonly GlBlendColor _blendColor;
    private readonly GlColorMask _colorMask;
    private readonly GlGenTextures _genTextures;
    private readonly GlBindTexture _bindTexture;
    private readonly GlTexImage2D _texImage2D;
    private readonly GlTexParameteri _texParameteri;
    private readonly GlActiveTexture _activeTexture;
    private readonly GlDeleteTextures _deleteTextures;
    private readonly GlBindFramebuffer _bindFramebuffer;
    private readonly GlGenBuffers _genBuffers;
    private readonly GlBindBuffer _bindBuffer;
    private readonly GlBufferData _bufferData;
    private readonly GlDeleteBuffers _deleteBuffers;
    private readonly GlCreateShader _createShader;
    private readonly GlShaderSource _shaderSource;
    private readonly GlCompileShader _compileShader;
    private readonly GlGetShaderiv _getShaderiv;
    private readonly GlGetShaderInfoLog _getShaderInfoLog;
    private readonly GlDeleteShader _deleteShader;
    private readonly GlCreateProgram _createProgram;
    private readonly GlAttachShader _attachShader;
    private readonly GlBindAttribLocation _bindAttribLocation;
    private readonly GlLinkProgram _linkProgram;
    private readonly GlGetProgramiv _getProgramiv;
    private readonly GlUseProgram _useProgram;
    private readonly GlDeleteProgram _deleteProgram;
    private readonly GlGetUniformLocation _getUniformLocation;
    private readonly GlUniform1i _uniform1i;
    private readonly GlUniform1f _uniform1f;
    private readonly GlVertexAttribPointer _vertexAttribPointer;
    private readonly GlEnableVertexAttribArray _enableVertexAttribArray;
    private readonly GlDisableVertexAttribArray _disableVertexAttribArray;
    private readonly GlDrawArrays _drawArrays;

    public GlBindings(GlInterface gl) : this(gl.GetProcAddress)
    {
    }

    public GlBindings(Func<string, IntPtr> getProcAddress)
    {
        T Bind<T>(string name) where T : Delegate
        {
            var ptr = getProcAddress(name);
            if (ptr == IntPtr.Zero) throw new InvalidOperationException($"GL entry point '{name}' is unavailable.");
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        _viewport = Bind<GlViewport>("glViewport");
        _clearColor = Bind<GlClearColor>("glClearColor");
        _clear = Bind<GlClear>("glClear");
        _enable = Bind<GlEnable>("glEnable");
        _disable = Bind<GlDisable>("glDisable");
        _blendFunc = Bind<GlBlendFunc>("glBlendFunc");
        _blendEquation = Bind<GlBlendEquation>("glBlendEquation");
        _blendColor = Bind<GlBlendColor>("glBlendColor");
        _colorMask = Bind<GlColorMask>("glColorMask");
        _genTextures = Bind<GlGenTextures>("glGenTextures");
        _bindTexture = Bind<GlBindTexture>("glBindTexture");
        _texImage2D = Bind<GlTexImage2D>("glTexImage2D");
        _texParameteri = Bind<GlTexParameteri>("glTexParameteri");
        _activeTexture = Bind<GlActiveTexture>("glActiveTexture");
        _deleteTextures = Bind<GlDeleteTextures>("glDeleteTextures");
        _bindFramebuffer = Bind<GlBindFramebuffer>("glBindFramebuffer");
        _genBuffers = Bind<GlGenBuffers>("glGenBuffers");
        _bindBuffer = Bind<GlBindBuffer>("glBindBuffer");
        _bufferData = Bind<GlBufferData>("glBufferData");
        _deleteBuffers = Bind<GlDeleteBuffers>("glDeleteBuffers");
        _createShader = Bind<GlCreateShader>("glCreateShader");
        _shaderSource = Bind<GlShaderSource>("glShaderSource");
        _compileShader = Bind<GlCompileShader>("glCompileShader");
        _getShaderiv = Bind<GlGetShaderiv>("glGetShaderiv");
        _getShaderInfoLog = Bind<GlGetShaderInfoLog>("glGetShaderInfoLog");
        _deleteShader = Bind<GlDeleteShader>("glDeleteShader");
        _createProgram = Bind<GlCreateProgram>("glCreateProgram");
        _attachShader = Bind<GlAttachShader>("glAttachShader");
        _bindAttribLocation = Bind<GlBindAttribLocation>("glBindAttribLocation");
        _linkProgram = Bind<GlLinkProgram>("glLinkProgram");
        _getProgramiv = Bind<GlGetProgramiv>("glGetProgramiv");
        _useProgram = Bind<GlUseProgram>("glUseProgram");
        _deleteProgram = Bind<GlDeleteProgram>("glDeleteProgram");
        _getUniformLocation = Bind<GlGetUniformLocation>("glGetUniformLocation");
        _uniform1i = Bind<GlUniform1i>("glUniform1i");
        _uniform1f = Bind<GlUniform1f>("glUniform1f");
        _vertexAttribPointer = Bind<GlVertexAttribPointer>("glVertexAttribPointer");
        _enableVertexAttribArray = Bind<GlEnableVertexAttribArray>("glEnableVertexAttribArray");
        _disableVertexAttribArray = Bind<GlDisableVertexAttribArray>("glDisableVertexAttribArray");
        _drawArrays = Bind<GlDrawArrays>("glDrawArrays");
    }

    public void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
    public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
    public void Clear(int mask) => _clear(mask);
    public void Enable(int cap) => _enable(cap);
    public void Disable(int cap) => _disable(cap);
    public void BlendFunc(int s, int d) => _blendFunc(s, d);
    public void BlendEquation(int mode) => _blendEquation(mode);
    public void BlendColor(float r, float g, float b, float a) => _blendColor(r, g, b, a);
    public void ColorMask(bool r, bool g, bool b, bool a) => _colorMask(r ? (byte)1 : (byte)0, g ? (byte)1 : (byte)0, b ? (byte)1 : (byte)0, a ? (byte)1 : (byte)0);

    public int GenTexture() { var a = new int[1]; _genTextures(1, a); return a[0]; }
    public void BindTexture(int texture) => _bindTexture(Texture2D, texture);
    public void TexImageRgba(int w, int h, IntPtr rgba) => _texImage2D(Texture2D, 0, Rgba, w, h, 0, Rgba, UnsignedByte, rgba);
    public void TexParameter(int pname, int param) => _texParameteri(Texture2D, pname, param);
    public void ActiveTexture(int texture) => _activeTexture(texture);
    public void DeleteTexture(int texture) => _deleteTextures(1, [texture]);

    public void BindFramebuffer(int fb) => _bindFramebuffer(Framebuffer, fb);

    public int GenBuffer() { var a = new int[1]; _genBuffers(1, a); return a[0]; }
    public void BindBuffer(int buffer) => _bindBuffer(ArrayBuffer, buffer);
    public void BufferData(int bytes, IntPtr data) => _bufferData(ArrayBuffer, new IntPtr(bytes), data, StreamDraw);
    public void DeleteBuffer(int buffer) => _deleteBuffers(1, [buffer]);

    public void UseProgram(int program) => _useProgram(program);
    public int GetUniform(int program, string name) => _getUniformLocation(program, name);
    public void Uniform1i(int loc, int v) => _uniform1i(loc, v);
    public void Uniform1f(int loc, float v) => _uniform1f(loc, v);

    public void VertexAttrib(int index, int size, int type, bool normalized, int stride, int offsetBytes)
        => _vertexAttribPointer(index, size, type, normalized ? (byte)1 : (byte)0, stride, new IntPtr(offsetBytes));
    public void EnableVertexAttrib(int index) => _enableVertexAttribArray(index);
    public void DisableVertexAttrib(int index) => _disableVertexAttribArray(index);
    public void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);

    /// <summary>Compiles and links a vertex/fragment program with fixed attribute slots; throws with the GL log on failure.</summary>
    public int BuildProgram(string vertexSource, string fragmentSource, params string[] attributes)
    {
        var vs = CompileShader(VertexShader, vertexSource);
        var fs = CompileShader(FragmentShader, fragmentSource);
        var program = _createProgram();
        _attachShader(program, vs);
        _attachShader(program, fs);
        for (var i = 0; i < attributes.Length; i++) _bindAttribLocation(program, i, attributes[i]);
        _linkProgram(program);

        var status = new int[1];
        _getProgramiv(program, LinkStatus, status);
        _deleteShader(vs);
        _deleteShader(fs);
        if (status[0] == 0)
        {
            _deleteProgram(program);
            throw new InvalidOperationException("PSP GU shader program failed to link.");
        }
        return program;
    }

    public void DeleteProgram(int program) => _deleteProgram(program);

    private int CompileShader(int type, string source)
    {
        var shader = _createShader(type);
        _shaderSource(shader, 1, [source], null);
        _compileShader(shader);

        var status = new int[1];
        _getShaderiv(shader, CompileStatus, status);
        if (status[0] == 0)
        {
            var sb = new StringBuilder(2048);
            _getShaderInfoLog(shader, sb.Capacity, out _, sb);
            _deleteShader(shader);
            throw new InvalidOperationException($"PSP GU shader failed to compile: {sb}");
        }
        return shader;
    }
}
