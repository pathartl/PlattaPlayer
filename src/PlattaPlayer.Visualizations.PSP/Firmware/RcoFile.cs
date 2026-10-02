using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>
/// Minimal reader for PSP RCO resource files (the "\0PRF" format), enough to pull the images out of
/// visualizer_plugin.rco. Layout from rcomage's rcofile.h (rcomage by ZiNgA BuRgA, LGPL 2.1) and the
/// project's tools/rco_rlz2zlib.py.
/// <para>
/// The 0xA4-byte header is followed by the entry tables (in flash0 RCOs LZR-compressed, then mapped at
/// virtual offset 0xA4) and the attached data. Each image entry is an RCOEntry (0x28 bytes, label
/// offset at +4) followed by an RCOImgModelEntry (format, compression, packed size, data offset,
/// unpacked size); the label is a C string in the label data. In 6.61 every image is an RLZ-compressed
/// GIM.
/// </para>
/// </summary>
internal static class RcoFile
{
    private const uint Signature = 0x46525000; // "\0PRF"
    private const int HeaderSize = 0xA4;
    private const int ComprNone = 0, ComprZlib = 1, ComprRlz = 2;
    private const int ImageFormatGim = 5;

    /// <summary>Returns the GIM images of the RCO, decompressed, keyed by resource name.</summary>
    public static Dictionary<string, byte[]> ReadGimImages(byte[] rco)
    {
        if (rco.Length < HeaderSize + 12 || BinaryPrimitives.ReadUInt32LittleEndian(rco) != Signature)
            throw new InvalidDataException("not an RCO file.");

        uint H(int field) => BinaryPrimitives.ReadUInt32LittleEndian(rco.AsSpan(field * 4));
        uint compression = H(3);
        uint pLabelData = H(16), lLabelData = H(17);
        uint pImgPtrs = H(22), lImgPtrs = H(23);
        uint pImgData = H(32);

        // The tables: either the file itself, or decompressed and mapped at virtual 0xA4.
        byte[] tables;
        int tableBase;
        switch ((int)(compression >> 4))
        {
            case ComprNone:
                tables = rco;
                tableBase = 0;
                break;
            case ComprZlib:
            case ComprRlz:
            {
                int lenPacked = BinaryPrimitives.ReadInt32LittleEndian(rco.AsSpan(HeaderSize));
                int lenUnpacked = BinaryPrimitives.ReadInt32LittleEndian(rco.AsSpan(HeaderSize + 4));
                if (lenPacked < 0 || lenUnpacked < 0 || HeaderSize + 12L + lenPacked > rco.Length)
                    throw new InvalidDataException("RCO header tables are truncated.");
                tables = Decompress((int)(compression >> 4), rco.AsSpan(HeaderSize + 12, lenPacked), lenUnpacked);
                if (tables.Length != lenUnpacked) throw new InvalidDataException("RCO header tables decompressed to the wrong size.");
                tableBase = HeaderSize;
                break;
            }
            default:
                throw new InvalidDataException($"unknown RCO compression 0x{compression:X}.");
        }

        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (pImgPtrs is 0 or 0xFFFFFFFF || lImgPtrs == 0) return images;

        for (uint i = 0; i < lImgPtrs / 4; i++)
        {
            uint ent = ReadU32(tables, pImgPtrs - tableBase + 4 * i);
            if (ent is 0 or 0xFFFFFFFF) continue;
            long e = ent - tableBase;
            uint labelOffset = ReadU32(tables, e + 4);
            long info = e + 0x28;
            int format = ReadU16(tables, info);
            int compr = ReadU16(tables, info + 2) & 0xFF;
            uint sizePacked = ReadU32(tables, info + 4);
            uint offset = ReadU32(tables, info + 8);
            if (format != ImageFormatGim) continue;

            string name = ReadLabel(tables, pLabelData - tableBase, lLabelData, labelOffset);
            long start = (long)pImgData + offset;
            if (start < 0 || start + sizePacked > rco.Length)
                throw new InvalidDataException($"RCO image \"{name}\" lies outside the file.");
            var packed = rco.AsSpan((int)start, (int)sizePacked);
            byte[] data = compr == ComprNone
                ? packed.ToArray()
                : Decompress(compr, packed, (int)ReadU32(tables, info + 12));
            images[name] = data;
        }
        return images;
    }

    private static byte[] Decompress(int compr, ReadOnlySpan<byte> packed, int unpackedSize)
    {
        switch (compr)
        {
            case ComprRlz:
                return Lzr.Decompress(packed, unpackedSize);
            case ComprZlib:
            {
                using var z = new ZLibStream(new MemoryStream(packed.ToArray(), writable: false), CompressionMode.Decompress);
                using var ms = new MemoryStream(unpackedSize);
                z.CopyTo(ms);
                return ms.ToArray();
            }
            default:
                throw new InvalidDataException($"unknown RCO data compression {compr}.");
        }
    }

    private static string ReadLabel(byte[] tables, long labelBase, uint labelLength, uint labelOffset)
    {
        long start = labelBase + labelOffset;
        if (labelOffset >= labelLength || start < 0 || start >= tables.Length)
            throw new InvalidDataException("RCO label offset out of range.");
        var s = tables.AsSpan((int)start, (int)Math.Min(tables.Length - start, labelLength - labelOffset));
        int end = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? s : s[..end]);
    }

    private static uint ReadU32(byte[] d, long offset)
    {
        if (offset < 0 || offset + 4 > d.Length) throw new InvalidDataException("RCO table offset out of range.");
        return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)offset));
    }

    private static ushort ReadU16(byte[] d, long offset)
    {
        if (offset < 0 || offset + 2 > d.Length) throw new InvalidDataException("RCO table offset out of range.");
        return BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)offset));
    }
}
