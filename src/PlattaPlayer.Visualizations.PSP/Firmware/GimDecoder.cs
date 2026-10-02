using System.Buffers.Binary;
using PlattaPlayer.Visualizations.PSP.Common;

namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>
/// Decodes a little-endian PSP GIM image (first image level / frame) to RGBA8888, a port of the
/// project's tools/gim2png.py. Handles the 16/32-bit direct formats and the 4/8/16/32-bit indexed
/// formats with a palette, and the PSP's swizzled ("faststriped") pixel order.
/// </summary>
internal static class GimDecoder
{
    private static readonly int[] Bpp = [16, 16, 16, 32, 4, 8, 16, 32];

    private sealed record GimImage(int Format, int Width, int Height, int Pitch, byte[] Data);

    /// <summary>Returns the pixels as 0xAABBGGRR (R in the low byte), row-major, pitch = width.</summary>
    public static PafImage Decode(byte[] gim)
    {
        if (gim.Length < 16 || !(gim.AsSpan(0, 4).SequenceEqual("MIG."u8) || gim.AsSpan(0, 4).SequenceEqual(".GIM"u8)))
            throw new InvalidDataException("not a little-endian GIM image.");

        GimImage? image = null, palette = null;
        Walk(gim, 16, gim.Length, ref image, ref palette, 0);
        if (image is null) throw new InvalidDataException("GIM has no image block.");

        int w = image.Width, h = image.Height;
        var pixels = new uint[w * h];
        if (image.Format >= 4)
        {
            if (palette is null) throw new InvalidDataException("indexed GIM has no palette.");
            var pal = new uint[palette.Width];
            for (int i = 0; i < pal.Length; i++) pal[i] = ToRgba(palette.Format, ReadValue(palette, 0, i));
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint v = ReadValue(image, y, x);
                pixels[y * w + x] = v < pal.Length ? pal[v] : 0;
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = ToRgba(image.Format, ReadValue(image, y, x));
        }
        return new PafImage(w, h, pixels);
    }

    private static void Walk(byte[] d, int off, int end, ref GimImage? image, ref GimImage? palette, int depth)
    {
        if (depth > 8) return;
        while (off + 16 <= end)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off));
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off + 4));
            int dataOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off + 12));
            if (size <= 0 || off + (long)size > d.Length) throw new InvalidDataException("GIM block size out of range.");
            switch (id)
            {
                case 2 or 3: // root / picture
                    Walk(d, off + dataOff, off + size, ref image, ref palette, depth + 1);
                    break;
                case 4 when image is null:
                    image = ParseImage(d, off + dataOff);
                    break;
                case 5 when palette is null:
                    palette = ParseImage(d, off + dataOff);
                    break;
            }
            off += size;
        }
    }

    private static GimImage ParseImage(byte[] d, int off)
    {
        if (off < 0 || off + 0x24 > d.Length) throw new InvalidDataException("GIM image header out of range.");
        var s = d.AsSpan(off);
        int fmt = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
        int order = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        int w = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]);
        int h = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]);
        int pitchAlign = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(s[14..]));
        int heightAlign = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(s[16..]));
        int pixStart = BinaryPrimitives.ReadInt32LittleEndian(s[0x1C..]);
        int pixEnd = BinaryPrimitives.ReadInt32LittleEndian(s[0x20..]);
        if (fmt >= Bpp.Length) throw new InvalidDataException($"GIM pixel format {fmt} is not supported.");

        int bpp = Bpp[fmt];
        int pitch = ((w * bpp / 8) + pitchAlign - 1) / pitchAlign * pitchAlign;
        int alignedHeight = (h + heightAlign - 1) / heightAlign * heightAlign;
        if (pixStart < 0 || pixEnd < pixStart || off + (long)pixEnd > d.Length)
            throw new InvalidDataException("GIM pixel data out of range.");
        var raw = d.AsSpan(off + pixStart, pixEnd - pixStart).ToArray();
        if ((long)pitch * h > raw.Length) throw new InvalidDataException("GIM pixel data is truncated.");
        if (order == 1) raw = Unswizzle(raw, pitch, alignedHeight);
        return new GimImage(fmt, w, h, pitch, raw);
    }

    /// <summary>PSP swizzled order: 16-byte x 8-row tiles.</summary>
    private static byte[] Unswizzle(byte[] data, int pitch, int height)
    {
        var output = new byte[data.Length];
        int src = 0;
        for (int ty = 0; ty < height; ty += 8)
        for (int tx = 0; tx < pitch; tx += 16)
        for (int y = 0; y < 8; y++)
        {
            if (ty + y < height)
            {
                int o = (ty + y) * pitch + tx;
                int n = Math.Min(16, Math.Min(data.Length - src, output.Length - o));
                if (n > 0) data.AsSpan(src, n).CopyTo(output.AsSpan(o));
            }
            src += 16;
        }
        return output;
    }

    private static uint ReadValue(GimImage img, int row, int i)
    {
        int bpp = Bpp[img.Format];
        int b = row * img.Pitch;
        var d = img.Data;
        return bpp switch
        {
            4 => (uint)(d[b + i / 2] >> (4 * (i & 1))) & 15,
            8 => d[b + i],
            16 => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(b + 2 * i)),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(b + 4 * i)),
        };
    }

    private static uint ToRgba(int fmt, uint v)
    {
        uint r, g, b, a;
        switch (fmt)
        {
            case 0: // 5650
                r = (v & 31) * 255 / 31;
                g = ((v >> 5) & 63) * 255 / 63;
                b = ((v >> 11) & 31) * 255 / 31;
                a = 255;
                break;
            case 1: // 5551
                r = (v & 31) * 255 / 31;
                g = ((v >> 5) & 31) * 255 / 31;
                b = ((v >> 10) & 31) * 255 / 31;
                a = (v >> 15) != 0 ? 255u : 0u;
                break;
            case 2: // 4444
                r = (v & 15) * 17;
                g = ((v >> 4) & 15) * 17;
                b = ((v >> 8) & 15) * 17;
                a = ((v >> 12) & 15) * 17;
                break;
            default: // 8888: already 0xAABBGGRR
                return v;
        }
        return r | g << 8 | b << 16 | a << 24;
    }
}
