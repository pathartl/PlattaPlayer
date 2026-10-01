using System.Buffers.Binary;
using System.IO.Compression;

namespace PlattaPlayer.Wmp.Harness.Imaging;

/// <summary>
/// Minimal PNG encoder (8-bit RGBA, no interlacing, filter type 0). Hand-rolled over ZLibStream so the
/// harness needs no image package — every dependency here is BCL.
/// </summary>
internal static class PngWriter
{
    /// <summary>Write BGRA (top-down, stride = width*4) pixels as an RGBA PNG.</summary>
    public static void WriteBgra(string path, ReadOnlySpan<byte> bgra, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Raw scanlines: each row prefixed with filter byte 0, channels reordered BGRA -> RGBA.
        var raw = new byte[height * (1 + width * 4)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0;
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                raw[o++] = bgra[i + 2];
                raw[o++] = bgra[i + 1];
                raw[o++] = bgra[i + 0];
                raw[o++] = bgra[i + 3];
            }
        }

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        var idat = ms.ToArray();

        using var fs = File.Create(path);
        fs.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: truecolour with alpha
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", idat);
        WriteChunk(fs, "IEND", []);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        Span<byte> tag = stackalloc byte[4];
        for (var i = 0; i < 4; i++) tag[i] = (byte)type[i];
        s.Write(tag);
        s.Write(data);

        var crc = Crc32(tag, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var v in a) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        foreach (var v in b) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
