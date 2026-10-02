using System.Buffers.Binary;
using System.Text;

namespace PlattaPlayer.Codecs.Usf.Psf;

/// <summary>
/// A PSF container file (the "Portable Sound Format" family; USF is version 0x21). Layout: "PSF", a version
/// byte, the reserved-area size, the compressed-program size and its CRC-32 (all little-endian 32-bit), then
/// the reserved area, the zlib-compressed program and, optionally, "[TAG]" followed by the tag text.
/// <para>A USF keeps everything in the reserved area (ROM chunks and a Project64 save state) and has no
/// program. The bytes before the tag are kept as they are, so rewriting the tags never touches them.</para>
/// </summary>
internal sealed class PsfFile
{
    public const byte UsfVersion = 0x21;

    private const int HeaderSize = 16;
    private static readonly byte[] TagMarker = "[TAG]"u8.ToArray();

    private readonly byte[] _data;
    private readonly int _reservedSize;
    private readonly int _tagOffset;

    private PsfFile(byte[] data, int reservedSize, int tagOffset, PsfTags tags)
    {
        _data = data;
        _reservedSize = reservedSize;
        _tagOffset = tagOffset;
        Tags = tags;
    }

    /// <summary>The reserved area: for a USF, the data handed to the emulator.</summary>
    public ReadOnlySpan<byte> Reserved => _data.AsSpan(HeaderSize, _reservedSize);

    public PsfTags Tags { get; }

    /// <summary>Parses a file's bytes; null when they aren't a PSF of <paramref name="version"/>.</summary>
    public static PsfFile? TryParse(byte[] data, byte version = UsfVersion)
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
        return new PsfFile(data, (int)reserved, tagOffset, tags);
    }

    public static PsfFile? TryLoad(string path, byte version = UsfVersion)
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
    /// megabytes. Null when the file isn't a PSF of <paramref name="version"/>.
    /// </summary>
    public static (int ReservedSize, PsfTags Tags)? TryReadTags(string path, byte version = UsfVersion)
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
            return ((int)reserved, tags);
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
}
