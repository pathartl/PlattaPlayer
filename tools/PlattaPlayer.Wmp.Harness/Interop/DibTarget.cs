namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// A memory DC with a 32-bpp top-down DIB section selected into it — the surface the real effects
/// render onto, and the one place their output can be read back byte-for-byte.
///
/// The DIB must be selected BEFORE any Render call. wmp.dll's Bars sizes its own internal DIB from
/// <c>max(GetDeviceCaps(hdc, BITSPIXEL), 8)</c>; a bare memory DC still has the default 1x1 monochrome
/// bitmap selected and reports 1 bpp, which would send Bars down an 8-bpp path that never runs inside
/// WMP. Selecting a 32-bpp DIB first makes BITSPIXEL report 32, matching the real host.
/// </summary>
internal sealed class DibTarget : IDisposable
{
    private nint _hdc;
    private nint _hBitmap;
    private nint _oldBitmap;
    private nint _bits;

    public int Width { get; }
    public int Height { get; }
    public nint Hdc => _hdc;

    public DibTarget(int width, int height)
    {
        Width = width;
        Height = height;

        _hdc = NativeMethods.CreateCompatibleDC(0);
        if (_hdc == 0) throw new InvalidOperationException("CreateCompatibleDC failed.");

        var bmi = new BITMAPINFOHEADER
        {
            biSize = 40,
            biWidth = width,
            biHeight = -height, // top-down, so row 0 is the top row and readback needs no flip
            biPlanes = 1,
            biBitCount = 32,
            biCompression = NativeMethods.BI_RGB,
        };

        _hBitmap = NativeMethods.CreateDIBSection(_hdc, in bmi, NativeMethods.DIB_RGB_COLORS, out _bits, 0, 0);
        if (_hBitmap == 0)
        {
            NativeMethods.DeleteDC(_hdc);
            _hdc = 0;
            throw new InvalidOperationException("CreateDIBSection failed.");
        }

        _oldBitmap = NativeMethods.SelectObject(_hdc, _hBitmap);
    }

    /// <summary>Colour depth the effect will observe. Must be 32 — see the class remarks.</summary>
    public int BitsPerPixel => NativeMethods.GetDeviceCaps(_hdc, NativeMethods.BITSPIXEL);

    /// <summary>Raw BGRX pixels, top-down, stride = Width*4. Alpha is undefined and must be normalised.</summary>
    public unsafe Span<byte> Pixels
    {
        get
        {
            NativeMethods.GdiFlush();
            return new Span<byte>((void*)_bits, Width * Height * 4);
        }
    }

    /// <summary>Snapshot the surface as BGRA with alpha forced to 0xFF, ready for diffing.</summary>
    public byte[] Snapshot()
    {
        var src = Pixels;
        var dst = new byte[src.Length];
        src.CopyTo(dst);
        for (var i = 3; i < dst.Length; i += 4) dst[i] = 0xFF;
        return dst;
    }

    /// <summary>Fill the surface with black. WHITENESS/BLACKNESS via PatBlt needs no brush.</summary>
    public void ClearBlack() => NativeMethods.PatBlt(_hdc, 0, 0, Width, Height, 0x00000042 /* BLACKNESS */);

    public void Dispose()
    {
        if (_hdc != 0)
        {
            if (_oldBitmap != 0) NativeMethods.SelectObject(_hdc, _oldBitmap);
            NativeMethods.DeleteDC(_hdc);
            _hdc = 0;
        }
        if (_hBitmap != 0)
        {
            NativeMethods.DeleteObject(_hBitmap);
            _hBitmap = 0;
        }
        _bits = 0;
    }
}
