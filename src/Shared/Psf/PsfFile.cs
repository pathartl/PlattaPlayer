using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PlattaPlayer.Codecs.Psf;

/// <summary>
/// A PSF container file (the "Portable Sound Format" family; the version byte tells the system: USF is 0x21,
/// GSF 0x22). Layout: "PSF", the version byte, the reserved-area size, the compressed-program size and its
/// CRC-32 (all little-endian 32-bit), then the reserved area, the zlib-compressed program and, optionally,
/// "[TAG]" followed by the tag text.
/// <para>Which section holds the music depends on the system: a USF keeps everything in the reserved area (ROM
/// chunks and a save state) and has no program; a GSF keeps its ROM data in the program. The bytes before the
/// tag are kept as they are, so rewriting the tags never touches them.</para>
/// </summary>
internal sealed class PsfFile
{
    public const byte UsfVersion = 0x21;
    public const byte GsfVersion = 0x22;

    private const int HeaderSize = 16;
    // Far above any real program (a GSF's is at most a 32 MiB ROM), so a corrupt or hostile file can't
    // inflate without limit.
    private const int MaxProgramSize = 64 << 20;
    private static readonly byte[] TagMarker = "[TAG]"u8.ToArray();

    private readonly byte[] _data;
    private readonly int _reservedSize;
    private readonly int _programSize;
    private readonly int _tagOffset;
    private byte[]? _program;

    private PsfFile(byte[] data, int reservedSize, int programSize, int tagOffset, PsfTags tags)
    {
        _data = data;
        _reservedSize = reservedSize;
        _programSize = programSize;
        _tagOffset = tagOffset;
        Tags = tags;
    }

    /// <summary>The reserved area: for a USF, the data handed to the emulator.</summary>
    public ReadOnlySpan<byte> Reserved => _data.AsSpan(HeaderSize, _reservedSize);

    /// <summary>The program, decompressed (empty when the file has none). Throws
    /// <see cref="InvalidDataException"/> when it doesn't decompress.</summary>
    public ReadOnlySpan<byte> Program => _program ??= Inflate(_data.AsSpan(HeaderSize + _reservedSize, _programSize));

    public PsfTags Tags { get; }

    /// <summary>Parses a file's bytes; null when they aren't a PSF of <paramref name="version"/>.</summary>
    public static PsfFile? TryParse(byte[] data, byte version)
    {
        if (data.Length < HeaderSize || data[0] != 'P' || data[1] != 'S' || data[2] != 'F' || data[3] != version)
            return null;

        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var program = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        var end = HeaderSize + (long)reserved + program;
        if (end > data.Length) return null;

        var tagOffset = (int)end;
        var tags = data.AsSpan(tagOffset).StartsWith(TagMarker)
            ? PsfTags.Parse(data.AsSpan(tagOffset + TagMarker.Length))
            : new PsfTags();
        return new PsfFile(data, (int)reserved, (int)program, tagOffset, tags);
    }

    public static PsfFile? TryLoad(string path, byte version)
    {
        try
        {
            return TryParse(File.ReadAllBytes(path), version);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads only what the library needs (the header and the tags), skipping the data, which can run to
    /// megabytes. <c>ProgramSize</c> is the compressed size. Null when the file isn't a PSF of
    /// <paramref name="version"/>.
    /// </summary>
    public static (int ReservedSize, int ProgramSize, PsfTags Tags)? TryReadTags(string path, byte version)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
            Span<byte> header = stackalloc byte[HeaderSize];
            if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize
                || header[0] != 'P' || header[1] != 'S' || header[2] != 'F' || header[3] != version)
                return null;

            var reserved = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var program = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var tagOffset = HeaderSize + (long)reserved + program;
            if (tagOffset > stream.Length) return null;

            stream.Position = tagOffset;
            var rest = new byte[stream.Length - tagOffset];
            stream.ReadExactly(rest);
            var tags = rest.AsSpan().StartsWith(TagMarker) ? PsfTags.Parse(rest.AsSpan(TagMarker.Length)) : new PsfTags();
            return ((int)reserved, (int)program, tags);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The file with its tag section replaced by <paramref name="tags"/> (written as UTF-8).</summary>
    public byte[] WithTags(PsfTags tags)
    {
        var text = tags.Format();
        if (text.Length == 0) return _data.AsSpan(0, _tagOffset).ToArray();

        var bytes = Encoding.UTF8.GetBytes(text);
        var result = new byte[_tagOffset + TagMarker.Length + bytes.Length];
        _data.AsSpan(0, _tagOffset).CopyTo(result);
        TagMarker.CopyTo(result.AsSpan(_tagOffset));
        bytes.CopyTo(result.AsSpan(_tagOffset + TagMarker.Length));
        return result;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        if (compressed.IsEmpty) return [];
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(compressed.ToArray()), CompressionMode.Decompress);
            var output = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = zlib.Read(chunk)) > 0)
            {
                if (output.Length + n > MaxProgramSize) throw new InvalidDataException("The PSF program is too large.");
                output.Write(chunk, 0, n);
            }
            return output.ToArray();
        }
        catch (IOException e)
        {
            throw new InvalidDataException("The PSF program is corrupt.", e);
        }
    }
}
