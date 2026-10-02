using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.OpenGL;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Plugin;

/// <summary>
/// A thin, version-independent binding to the handful of OpenGL (ES) 2.0 entry points the WMP Alchemy GPU
/// renderer needs, resolved through Avalonia's <see cref="GlInterface.GetProcAddress"/>. Binding our own
/// delegates (rather than relying on <see cref="GlInterface"/>'s typed surface) keeps this stable across
/// Avalonia versions and across the desktop-GL / ANGLE-GLES backends Avalonia may select. (A near-verbatim
/// copy of the MilkDrop plugin's binding, which is internal to that assembly; this copy additionally
/// exposes the array uniforms, pixel upload and read-back.) It can also be built from any proc-address
/// loader, which is how the harness runs the same renderer in its own offscreen context.
/// </summary>
internal sealed class GlBindings
{
    // GLenum / GLbitfield constants used below.
    public const int ColorBufferBit = 0x4000;
    public const int Texture2D = 0x0DE1;
    public const int Rgba = 0x1908;
    public const int UnsignedByte = 0x1401;
    public const int Float = 0x1406;
    public const int TextureMagFilter = 0x2800;
    public const int TextureMinFilter = 0x2801;
    public const int TextureWrapS = 0x2802;
    public const int TextureWrapT = 0x2803;
    public const int Nearest = 0x2600;
    public const int Linear = 0x2601;
    public const int ClampToEdge = 0x812F;
    public const int Framebuffer = 0x8D40;
    public const int ColorAttachment0 = 0x8CE0;
    public const int ArrayBuffer = 0x8892;
    public const int ElementArrayBuffer = 0x8893;
    public const int DynamicDraw = 0x88E8;
    public const int StaticDraw = 0x88E4;
    public const int UnsignedShort = 0x1403;
    public const int FragmentShader = 0x8B30;
    public const int VertexShader = 0x8B31;
    public const int CompileStatus = 0x8B81;
    public const int LinkStatus = 0x8B82;
    public const int Points = 0;
    public const int Lines = 1;
    public const int LineStrip = 3;
    public const int Triangles = 4;
    public const int TriangleStrip = 5;
    public const int Blend = 0x0BE2;
    public const int SrcAlpha = 0x0302;
    public const int OneMinusSrcAlpha = 0x0303;
    public const int One = 1;
    public const int Texture0 = 0x84C0;
    public const int ViewportPName = 0x0BA2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlViewport(int x, int y, int w, int h);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlClearColor(float r, float g, float b, float a);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlClear(int mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlEnable(int cap);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDisable(int cap);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBlendFunc(int s, int d);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetIntegerv(int pname, int[] data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGenTextures(int n, int[] textures);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBindTexture(int target, int texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlTexImage2D(int target, int level, int internalFormat, int w, int h, int border, int format, int type, IntPtr pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlTexParameteri(int target, int pname, int param);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlActiveTexture(int texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteTextures(int n, int[] textures);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGenFramebuffers(int n, int[] framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBindFramebuffer(int target, int framebuffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteFramebuffers(int n, int[] framebuffers);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGenBuffers(int n, int[] buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBindBuffer(int target, int buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlBufferData(int target, IntPtr size, float[] data, int usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteBuffers(int n, int[] buffers);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GlCreateShader(int type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlShaderSource(int shader, int count, string[] strings, int[]? lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlCompileShader(int shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetShaderiv(int shader, int pname, int[] @params);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetShaderInfoLog(int shader, int maxLength, out int length, StringBuilder infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteShader(int shader);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GlCreateProgram();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlAttachShader(int program, int shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlLinkProgram(int program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlGetProgramiv(int program, int pname, int[] @params);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUseProgram(int program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDeleteProgram(int program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GlGetUniformLocation(int program, string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GlGetAttribLocation(int program, string name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform1i(int location, int v0);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform1f(int location, float v0);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform2f(int location, float v0, float v1);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform3f(int location, float v0, float v1, float v2);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform4f(int location, float v0, float v1, float v2, float v3);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform1fv(int location, int count, float[] value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlUniform4fv(int location, int count, float[] value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlTexImage2DData(int target, int level, int internalFormat, int w, int h, int border, int format, int type, byte[] pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlReadPixels(int x, int y, int w, int h, int format, int type, byte[] pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDisableVertexAttribArray(int index);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlVertexAttribPointer(int index, int size, int type, byte normalized, int stride, IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlEnableVertexAttribArray(int index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GlDrawArrays(int mode, int first, int count);

    private readonly GlViewport _viewport;
    private readonly GlClearColor _clearColor;
    private readonly GlClear _clear;
    private readonly GlEnable _enable;
    private readonly GlDisable _disable;
    private readonly GlBlendFunc _blendFunc;
    private readonly GlGetIntegerv _getIntegerv;
    private readonly GlGenTextures _genTextures;
    private readonly GlBindTexture _bindTexture;
    private readonly GlTexImage2D _texImage2D;
    private readonly GlTexParameteri _texParameteri;
    private readonly GlActiveTexture _activeTexture;
    private readonly GlDeleteTextures _deleteTextures;
    private readonly GlGenFramebuffers _genFramebuffers;
    private readonly GlBindFramebuffer _bindFramebuffer;
    private readonly GlFramebufferTexture2D _framebufferTexture2D;
    private readonly GlDeleteFramebuffers _deleteFramebuffers;
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
    private readonly GlLinkProgram _linkProgram;
    private readonly GlGetProgramiv _getProgramiv;
    private readonly GlUseProgram _useProgram;
    private readonly GlDeleteProgram _deleteProgram;
    private readonly GlGetUniformLocation _getUniformLocation;
    private readonly GlGetAttribLocation _getAttribLocation;
    private readonly GlUniform1i _uniform1i;
    private readonly GlUniform1f _uniform1f;
    private readonly GlUniform2f _uniform2f;
    private readonly GlUniform3f _uniform3f;
    private readonly GlUniform4f _uniform4f;
    private readonly GlUniform1fv _uniform1fv;
    private readonly GlUniform4fv _uniform4fv;
    private readonly GlTexImage2DData _texImage2DData;
    private readonly GlReadPixels _readPixels;
    private readonly GlDisableVertexAttribArray _disableVertexAttribArray;
    private readonly GlVertexAttribPointer _vertexAttribPointer;
    private readonly GlEnableVertexAttribArray _enableVertexAttribArray;
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
        _getIntegerv = Bind<GlGetIntegerv>("glGetIntegerv");
        _genTextures = Bind<GlGenTextures>("glGenTextures");
        _bindTexture = Bind<GlBindTexture>("glBindTexture");
        _texImage2D = Bind<GlTexImage2D>("glTexImage2D");
        _texParameteri = Bind<GlTexParameteri>("glTexParameteri");
        _activeTexture = Bind<GlActiveTexture>("glActiveTexture");
        _deleteTextures = Bind<GlDeleteTextures>("glDeleteTextures");
        _genFramebuffers = Bind<GlGenFramebuffers>("glGenFramebuffers");
        _bindFramebuffer = Bind<GlBindFramebuffer>("glBindFramebuffer");
        _framebufferTexture2D = Bind<GlFramebufferTexture2D>("glFramebufferTexture2D");
        _deleteFramebuffers = Bind<GlDeleteFramebuffers>("glDeleteFramebuffers");
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
        _linkProgram = Bind<GlLinkProgram>("glLinkProgram");
        _getProgramiv = Bind<GlGetProgramiv>("glGetProgramiv");
        _useProgram = Bind<GlUseProgram>("glUseProgram");
        _deleteProgram = Bind<GlDeleteProgram>("glDeleteProgram");
        _getUniformLocation = Bind<GlGetUniformLocation>("glGetUniformLocation");
        _getAttribLocation = Bind<GlGetAttribLocation>("glGetAttribLocation");
        _uniform1i = Bind<GlUniform1i>("glUniform1i");
        _uniform1f = Bind<GlUniform1f>("glUniform1f");
        _uniform2f = Bind<GlUniform2f>("glUniform2f");
        _uniform3f = Bind<GlUniform3f>("glUniform3f");
        _uniform4f = Bind<GlUniform4f>("glUniform4f");
        _uniform1fv = Bind<GlUniform1fv>("glUniform1fv");
        _uniform4fv = Bind<GlUniform4fv>("glUniform4fv");
        _texImage2DData = Bind<GlTexImage2DData>("glTexImage2D");
        _readPixels = Bind<GlReadPixels>("glReadPixels");
        _disableVertexAttribArray = Bind<GlDisableVertexAttribArray>("glDisableVertexAttribArray");
        _vertexAttribPointer = Bind<GlVertexAttribPointer>("glVertexAttribPointer");
        _enableVertexAttribArray = Bind<GlEnableVertexAttribArray>("glEnableVertexAttribArray");
        _drawArrays = Bind<GlDrawArrays>("glDrawArrays");
    }

    public void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
    public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
    public void Clear(int mask) => _clear(mask);
    public void Enable(int cap) => _enable(cap);
    public void Disable(int cap) => _disable(cap);
    public void BlendFunc(int s, int d) => _blendFunc(s, d);

    public (int Width, int Height) GetViewportSize()
    {
        var data = new int[4];
        _getIntegerv(ViewportPName, data);
        return (data[2], data[3]);
    }

    public int GenTexture() { var a = new int[1]; _genTextures(1, a); return a[0]; }
    public void BindTexture(int target, int texture) => _bindTexture(target, texture);
    public void TexImageEmpty(int w, int h) => _texImage2D(Texture2D, 0, Rgba, w, h, 0, Rgba, UnsignedByte, IntPtr.Zero);
    /// <summary>Uploads tightly packed RGBA bytes, bottom row first.</summary>
    public void TexImageRgba(int w, int h, byte[] rgba) => _texImage2DData(Texture2D, 0, Rgba, w, h, 0, Rgba, UnsignedByte, rgba);
    public void TexParameter(int pname, int param) => _texParameteri(Texture2D, pname, param);
    public void ActiveTexture(int texture) => _activeTexture(texture);
    public void DeleteTexture(int texture) => _deleteTextures(1, [texture]);

    public int GenFramebuffer() { var a = new int[1]; _genFramebuffers(1, a); return a[0]; }
    public void BindFramebuffer(int fb) => _bindFramebuffer(Framebuffer, fb);
    public void AttachColorTexture(int texture) => _framebufferTexture2D(Framebuffer, ColorAttachment0, Texture2D, texture, 0);
    public void DeleteFramebuffer(int fb) => _deleteFramebuffers(1, [fb]);

    public int GenBuffer() { var a = new int[1]; _genBuffers(1, a); return a[0]; }
    public void BindBuffer(int target, int buffer) => _bindBuffer(target, buffer);
    public void BufferData(int target, float[] data, int usage) => _bufferData(target, new IntPtr(data.Length * sizeof(float)), data, usage);
    /// <summary>Uploads only the first <paramref name="floatCount"/> floats of a possibly over-sized array.</summary>
    public void BufferDataRange(int target, float[] data, int floatCount, int usage) => _bufferData(target, new IntPtr(floatCount * sizeof(float)), data, usage);
    public void DeleteBuffer(int buffer) => _deleteBuffers(1, [buffer]);

    public void UseProgram(int program) => _useProgram(program);
    public int GetUniform(int program, string name) => _getUniformLocation(program, name);
    public int GetAttrib(int program, string name) => _getAttribLocation(program, name);
    public void Uniform1i(int loc, int v) => _uniform1i(loc, v);
    public void Uniform1f(int loc, float v) => _uniform1f(loc, v);
    public void Uniform2f(int loc, float a, float b) => _uniform2f(loc, a, b);
    public void Uniform3f(int loc, float a, float b, float c) => _uniform3f(loc, a, b, c);
    public void Uniform4f(int loc, float a, float b, float c, float d) => _uniform4f(loc, a, b, c, d);
    /// <summary>Uploads a float array uniform (the GLSL <c>uWarpA[]</c>/<c>uWarpB[]</c> warp params).</summary>
    public void Uniform1fv(int loc, float[] v) => _uniform1fv(loc, v.Length, v);
    /// <summary>Uploads <paramref name="count"/> vec4s from a packed float array.</summary>
    public void Uniform4fv(int loc, int count, float[] v) => _uniform4fv(loc, count, v);

    /// <summary>Reads the bound framebuffer as tightly packed RGBA bytes, bottom row first.</summary>
    public void ReadPixelsRgba(int x, int y, int w, int h, byte[] rgba) => _readPixels(x, y, w, h, Rgba, UnsignedByte, rgba);

    public void VertexAttribFloat(int index, int size, int stride, int offsetFloats)
        => _vertexAttribPointer(index, size, Float, 0, stride * sizeof(float), new IntPtr(offsetFloats * sizeof(float)));
    public void EnableVertexAttrib(int index) => _enableVertexAttribArray(index);
    public void DisableVertexAttrib(int index) => _disableVertexAttribArray(index);
    public void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);

    /// <summary>Compiles and links a vertex/fragment program; throws with the GL log on failure.</summary>
    public int BuildProgram(string vertexSource, string fragmentSource)
    {
        var vs = CompileShader(VertexShader, vertexSource);
        var fs = CompileShader(FragmentShader, fragmentSource);
        var program = _createProgram();
        _attachShader(program, vs);
        _attachShader(program, fs);
        _linkProgram(program);

        var status = new int[1];
        _getProgramiv(program, LinkStatus, status);
        _deleteShader(vs);
        _deleteShader(fs);
        if (status[0] == 0)
        {
            _deleteProgram(program);
            throw new InvalidOperationException("WMP Alchemy shader program failed to link.");
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
            throw new InvalidOperationException($"WMP Alchemy shader failed to compile: {sb}");
        }
        return shader;
    }
}
