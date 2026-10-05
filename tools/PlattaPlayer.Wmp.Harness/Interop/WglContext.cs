using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// A hidden window with a plain WGL OpenGL context, current on the calling thread, so the harness can
/// run the app's GPU renderer offscreen (it only ever renders into its own framebuffer objects).
///
/// The legacy <c>wglCreateContext</c> gives a compatibility context at the driver's highest version, which
/// runs the renderer's <c>#version 120</c> shaders. The app usually runs the GLSL ES flavour through
/// ANGLE instead, so this checks the shader LOGIC, not that particular compiler.
/// </summary>
internal sealed class WglContext : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly IntPtr _hdc;
    private readonly IntPtr _hglrc;
    private readonly IntPtr _opengl32;

    public WglContext()
    {
        _hwnd = CreateWindowExW(0, "STATIC", "alchemy-gl", 0, 0, 0, 16, 16, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed.");
        _hdc = GetDC(_hwnd);

        var pfd = new PixelFormatDescriptor
        {
            nSize = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(),
            nVersion = 1,
            dwFlags = 0x4 | 0x20 | 0x1, // DRAW_TO_WINDOW | SUPPORT_OPENGL | DOUBLEBUFFER
            iPixelType = 0,             // RGBA
            cColorBits = 32,
            cDepthBits = 0,
            iLayerType = 0,
        };
        var format = ChoosePixelFormat(_hdc, ref pfd);
        if (format == 0 || !SetPixelFormat(_hdc, format, ref pfd)) throw new InvalidOperationException("No usable pixel format.");

        _hglrc = wglCreateContext(_hdc);
        if (_hglrc == IntPtr.Zero || !wglMakeCurrent(_hdc, _hglrc)) throw new InvalidOperationException("wglCreateContext failed.");

        _opengl32 = LoadLibraryW("opengl32.dll");
    }

    /// <summary>
    /// Entry points: GL 1.2+ come from <c>wglGetProcAddress</c>, which returns null (or one of a few
    /// small sentinel values) for the GL 1.1 core exported straight from opengl32.dll.
    /// </summary>
    public IntPtr GetProcAddress(string name)
    {
        var p = wglGetProcAddress(name);
        var v = p.ToInt64();
        if (v is 0 or 1 or 2 or 3 or -1) p = GetProcAddressA(_opengl32, name);
        return p;
    }

    public void Dispose()
    {
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (_hglrc != IntPtr.Zero) wglDeleteContext(_hglrc);
        if (_hdc != IntPtr.Zero) ReleaseDC(_hwnd, _hdc);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
                    cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits,
                    cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr hdc, ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PixelFormatDescriptor pfd);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetProcAddress")]
    private static extern IntPtr GetProcAddressA(IntPtr module, string name);
}
