using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

internal static partial class NativeMethods
{
    public const uint CLSCTX_INPROC_SERVER = 0x1;
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    /// <summary>GetDeviceCaps index for colour depth. Bars sizes its own DIB from this.</summary>
    public const int BITSPIXEL = 12;

    public const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

    // ---- ole32 -------------------------------------------------------------------------------

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    // ---- oleaut32 ----------------------------------------------------------------------------

    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(nint bstr);

    // ---- kernel32 ----------------------------------------------------------------------------

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16,
                   SetLastError = true)]
    public static partial nint LoadLibraryEx(string lpLibFileName, nint hFile, uint dwFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string lpModuleName);

    public const uint PAGE_EXECUTE_READWRITE = 0x40;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushInstructionCache(nint hProcess, nint lpBaseAddress, nuint dwSize);

    // ---- gdi32 -------------------------------------------------------------------------------

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(
        nint hdc, in BITMAPINFOHEADER pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDeviceCaps(nint hdc, int index);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PatBlt(nint hdc, int x, int y, int w, int h, uint rop);

    // ---- user32 ------------------------------------------------------------------------------

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    /// <summary>PrintWindow flag: render the full window content, including DirectComposition surfaces.</summary>
    public const uint PW_RENDERFULLCONTENT = 0x2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumChildWindows(nint hWndParent, nint lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ---- ucrt --------------------------------------------------------------------------------

    /// <summary>
    /// ucrtbase's rand()/srand() keep a PER-THREAD seed. wmp.dll's Bars renderer adds
    /// <c>rand()*20/32767 - 10</c> px of jitter per bar whenever TimedLevel.state == 2; if wmp.dll
    /// resolves rand through the same ucrtbase this call reaches, seeding here on the capture thread
    /// makes its jitter reproducible and lets us diff Bars bit-exactly.
    /// </summary>
    [LibraryImport("ucrtbase.dll", EntryPoint = "srand")]
    public static partial void UcrtSrand(uint seed);

    [LibraryImport("ucrtbase.dll", EntryPoint = "rand")]
    public static partial int UcrtRand();
}
