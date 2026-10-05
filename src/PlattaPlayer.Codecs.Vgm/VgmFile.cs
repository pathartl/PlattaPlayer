using System.Buffers.Binary;
using System.IO.Compression;

namespace PlattaPlayer.Codecs.Vgm;

/// <summary>
/// A VGM file: a header naming the sound chips and their clocks, a log of their register writes timed in
/// 44100 Hz samples (with an optional loop point), and a GD3 tag. A .vgz is the same, gzipped; a file is
/// recognised by its content, not its extension.
/// </summary>
internal sealed class VgmFile
{
    /// <summary>The rate VGM times its commands in.</summary>
    public const int SampleRate = 44100;

    // Header offsets (VGM 1.71). Offsets stored in the header are relative to their own position.
    private const int EofOffset = 0x04;
    private const int VersionOffset = 0x08;
    private const int Gd3OffsetField = 0x14;
    private const int TotalSamplesOffset = 0x18;
    private const int LoopOffsetField = 0x1C;
    private const int LoopSamplesOffset = 0x20;
    private const int DataOffsetField = 0x34;
    private const int LoopBaseOffset = 0x7E;
    private const int LoopModifierOffset = 0x7F;

    /// <summary>The header of a file older than 1.50, which has no data offset.</summary>
    private const int LegacyHeaderSize = 0x40;

    /// <summary>All header fields of the newest version fit in this.</summary>
    private const int MaxHeaderSize = 0x100;

    // Header clock fields and the chips they name. Bit 30 of a clock marks a second chip of the type.
    private static readonly (int Offset, string Name)[] ChipClocks =
    [
        (0x0C, "SN76489"), (0x10, "YM2413"), (0x2C, "YM2612"), (0x30, "YM2151"), (0x38, "SegaPCM"), (0x40, "RF5C68"),
        (0x44, "YM2203"), (0x48, "YM2608"), (0x4C, "YM2610"), (0x50, "YM3812"), (0x54, "YM3526"), (0x58, "Y8950"),
        (0x5C, "YMF262"), (0x60, "YMF278B"), (0x64, "YMF271"), (0x68, "YMZ280B"), (0x6C, "RF5C164"), (0x70, "32X PWM"),
        (0x74, "AY8910"), (0x80, "Game Boy DMG"), (0x84, "NES APU"), (0x88, "MultiPCM"), (0x8C, "uPD7759"),
        (0x90, "OKIM6258"), (0x98, "OKIM6295"), (0x9C, "K051649"), (0xA0, "K054539"), (0xA4, "HuC6280"), (0xA8, "C140"),
        (0xAC, "K053260"), (0xB0, "POKEY"), (0xB4, "QSound"), (0xB8, "SCSP"), (0xC0, "WonderSwan"), (0xC4, "VSU"),
        (0xC8, "SAA1099"), (0xCC, "ES5503"), (0xD0, "ES5506"), (0xD8, "X1-010"), (0xDC, "C352"), (0xE0, "GA20"),
        (0xE4, "Mikey"),
    ];

    /// <summary>The OPL4's clock: such a song may need the YRW801 sample ROM.</summary>
    private const int Ymf278bClockOffset = 0x60;

    private VgmFile(byte[] header, Gd3Tag? gd3)
    {
        Header = header;
        Gd3 = gd3;
    }

    /// <summary>The header, zero-filled past its real size to <see cref="MaxHeaderSize"/> bytes.</summary>
    private byte[] Header { get; }

    public Gd3Tag? Gd3 { get; }

    /// <summary>The file version, e.g. 0x171 for 1.71.</summary>
    public uint Version => U32(VersionOffset);

    /// <summary>Samples from the start to the end of the log.</summary>
    public long TotalSamples => U32(TotalSamplesOffset);

    /// <summary>Samples in the looped section; 0 when the song doesn't loop.</summary>
    public long LoopSamples => U32(LoopOffsetField) != 0 ? U32(LoopSamplesOffset) : 0;

    public bool Loops => LoopSamples > 0;

    /// <summary>Whether the song uses the OPL4, whose wavetable is in a separate sample ROM.</summary>
    public bool UsesOpl4 => U32(Ymf278bClockOffset) != 0;

    /// <summary>The sound chips the song plays on, e.g. "YM2612 + SN76489".</summary>
    public string ChipNames
    {
        get
        {
            var names = new List<string>();
            foreach (var (offset, name) in ChipClocks)
            {
                var clock = U32(offset);
                if ((clock & 0x3FFFFFFF) == 0) continue;
                var chip = offset switch
                {
                    0x4C when (clock & 0x80000000) != 0 => "YM2610B",
                    0x0C when (clock & 0x80000000) != 0 => "T6W28",
                    _ => name,
                };
                names.Add((clock & 0x40000000) != 0 ? $"2× {chip}" : chip);
            }
            return names.Count > 0 ? string.Join(" + ", names) : "no sound chips";
        }
    }

    /// <summary>
    /// How many times the looped section plays, given the player's default: the file can scale the default
    /// (loop modifier, in 1/16ths) and subtract from it (loop base), for songs whose loop is unusually short or
    /// long. At least once. Matches libvgm's VGMPlayer::GetModifiedLoopCount.
    /// </summary>
    public int LoopCount(int defaultLoops)
    {
        var modifier = Header[LoopModifierOffset];
        var loopBase = (sbyte)Header[LoopBaseOffset];
        var loops = modifier != 0 ? (defaultLoops * modifier + 8) / 16 : defaultLoops;
        return loops <= loopBase ? 1 : loops - loopBase;
    }

    /// <summary>
    /// The header and GD3 tag of a VGM / VGZ file, read without loading the whole log where possible. Null
    /// when it isn't a VGM file or can't be read.
    /// </summary>
    public static VgmFile? TryRead(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (!IsGzip(file)) return Read(file, seekable: true);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return Read(gzip, seekable: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The header and GD3 tag of an uncompressed VGM in memory, or null.</summary>
    public static VgmFile? Parse(byte[] data) => Read(new MemoryStream(data, writable: false), seekable: true);

    private static VgmFile? Read(Stream stream, bool seekable)
    {
        var header = new byte[MaxHeaderSize];
        var read = stream.ReadAtLeast(header, LegacyHeaderSize, throwOnEndOfStream: false);
        if (read < LegacyHeaderSize || !IsVgm(header)) return null;
        read += stream.ReadAtLeast(header.AsSpan(read), header.Length - read, throwOnEndOfStream: false);

        var gd3Position = Gd3PositionOf(header);
        Gd3Tag? gd3 = null;
        if (gd3Position > 0 && seekable)
        {
            stream.Position = gd3Position;
            gd3 = Gd3Tag.TryRead(stream);
        }
        else if (gd3Position >= read)
        {
            if (Skip(stream, gd3Position - read)) gd3 = Gd3Tag.TryRead(stream);
        }
        else if (gd3Position > 0)
        {
            // A tag inside the first bytes read (only in a malformed file).
            gd3 = Gd3Tag.TryRead(new MemoryStream(header, (int)gd3Position, read - (int)gd3Position));
        }

        // Fields past the header's real end are absent: read them as 0.
        var headerSize = HeaderSizeOf(header, read);
        Array.Clear(header, headerSize, header.Length - headerSize);
        return new VgmFile(header, gd3);
    }

    /// <summary>The whole file, decompressed. Null when it isn't a VGM file.</summary>
    public static (byte[] Data, bool Gzipped)? TryLoad(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var gzipped = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        if (gzipped)
        {
            try
            {
                using var output = new MemoryStream(bytes.Length * 4);
                using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
                    gzip.CopyTo(output);
                bytes = output.ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
        return bytes.Length >= LegacyHeaderSize && IsVgm(bytes) ? (bytes, gzipped) : null;
    }

    /// <summary>
    /// The file's bytes with <paramref name="tag"/> as its GD3 tag (none when null). A tag at the end of the
    /// file, where every rip has it, is replaced; one anywhere else is left in place unreferenced, and the new
    /// one appended.
    /// </summary>
    public static byte[] WithGd3(byte[] data, Gd3Tag? tag)
    {
        var header = data.AsSpan(0, Math.Min(data.Length, MaxHeaderSize)).ToArray();
        var oldPosition = Gd3PositionOf(header);
        var keep = data.Length;
        if (oldPosition > 0 && oldPosition < data.Length
            && Gd3Tag.EndOf(data.AsSpan((int)oldPosition)) is { } length && oldPosition + length >= data.Length)
            keep = (int)oldPosition;

        var encoded = tag?.Encode() ?? [];
        var result = new byte[keep + encoded.Length];
        data.AsSpan(0, keep).CopyTo(result);
        encoded.CopyTo(result, keep);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(Gd3OffsetField), tag is null ? 0u : (uint)(keep - Gd3OffsetField));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(EofOffset), (uint)(result.Length - EofOffset));
        return result;
    }

    /// <summary>Gzips data for a .vgz.</summary>
    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 2);
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(data);
        return output.ToArray();
    }

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(offset));

    private static bool IsVgm(ReadOnlySpan<byte> data) => data[..4].SequenceEqual("Vgm "u8);

    private static bool IsGzip(FileStream file)
    {
        Span<byte> magic = stackalloc byte[2];
        var gzip = file.ReadAtLeast(magic, 2, throwOnEndOfStream: false) == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
        file.Position = 0;
        return gzip;
    }

    // Before 1.50 the log starts at 0x40; from 1.50 the header says where (its fields end there).
    private static int HeaderSizeOf(ReadOnlySpan<byte> header, int read)
    {
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[VersionOffset..]);
        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[DataOffsetField..]);
        var size = version >= 0x150 && dataOffset != 0 ? DataOffsetField + (long)dataOffset : LegacyHeaderSize;
        return (int)Math.Clamp(size, LegacyHeaderSize, Math.Min(read, MaxHeaderSize));
    }

    private static long Gd3PositionOf(ReadOnlySpan<byte> header)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(header[Gd3OffsetField..]);
        return offset == 0 ? 0 : Gd3OffsetField + (long)offset;
    }

    private static bool Skip(Stream stream, long count)
    {
        var buffer = new byte[64 << 10];
        while (count > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) return false;
            count -= read;
        }
        return true;
    }
}
