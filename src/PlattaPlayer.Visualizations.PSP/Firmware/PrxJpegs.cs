using System.Buffers.Binary;

namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>
/// Finds the two JPEGs embedded in the data section of the decrypted visualizer_plugin.prx (an ELF
/// whose file offset = address + 0xC0 below 0x14a48): the 64x64 cloud palette at 0x13850 (0x55f bytes,
/// opened by the type 3/6 CloudEmitter) and the 120x68 image at 0x13e00 (0x424 bytes, decoded by the
/// type 7 constructor). The known 6.61 locations are tried first and validated (SOI at the start, EOI
/// at the end, dimensions from the SOF marker); otherwise the file is scanned for a JPEG with the
/// expected dimensions, so other firmware revisions with shifted data still work.
/// </summary>
internal static class PrxJpegs
{
    private const int FileOffset = 0xC0;

    public const int CloudAddress = 0x13850, CloudLength = 0x55F, CloudWidth = 64, CloudHeight = 64;
    public const int SandAddress = 0x13E00, SandLength = 0x424, SandWidth = 120, SandHeight = 68;

    /// <summary>Returns the JPEG bytes or null when no JPEG with those dimensions exists in the PRX.</summary>
    public static byte[]? Find(byte[] prx, int address, int length, int width, int height)
    {
        int at = address + FileOffset;
        if (at + length <= prx.Length && TryParse(prx, at, out int len, out int w, out int h) &&
            len == length && w == width && h == height)
            return prx.AsSpan(at, length).ToArray();

        for (int i = 0; i + 4 <= prx.Length; i++)
        {
            if (prx[i] != 0xFF || prx[i + 1] != 0xD8 || prx[i + 2] != 0xFF) continue;
            if (TryParse(prx, i, out len, out w, out h) && w == width && h == height)
                return prx.AsSpan(i, len).ToArray();
        }
        return null;
    }

    /// <summary>
    /// Walks the JPEG marker segments from an SOI at <paramref name="start"/> to its EOI. Returns the total
    /// length (SOI..EOI inclusive) and the frame size from the first SOF segment.
    /// </summary>
    internal static bool TryParse(byte[] d, int start, out int length, out int width, out int height)
    {
        length = width = height = 0;
        if (start + 4 > d.Length || d[start] != 0xFF || d[start + 1] != 0xD8) return false;
        int p = start + 2;
        bool haveFrame = false;
        while (p + 2 <= d.Length)
        {
            if (d[p] != 0xFF) return false;
            byte marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; } // fill byte
            if (marker == 0xD9) break;             // EOI
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { p += 2; continue; }
            if (p + 4 > d.Length) return false;
            int segLen = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 2));
            if (segLen < 2 || p + 2 + segLen > d.Length) return false;

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && !haveFrame)
            {
                if (segLen < 7) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 5));
                width = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 7));
                haveFrame = true;
            }

            p += 2 + segLen;
            if (marker == 0xDA)
            {
                // entropy-coded data: FF is followed by 00 (stuffing) or RSTn inside the scan
                while (p + 1 < d.Length)
                {
                    if (d[p] == 0xFF && d[p + 1] != 0x00 && d[p + 1] is not (>= 0xD0 and <= 0xD7)) break;
                    p++;
                }
            }
        }
        if (p + 2 > d.Length || !haveFrame) return false;
        length = p + 2 - start;
        return true;
    }
}
