using System.Buffers.Binary;
using System.Text;

namespace PlattaPlayer.Codecs.Vgm;

/// <summary>
/// A VGM file's GD3 tag (version 1.00): eleven UTF-16 strings, most in an English and a Japanese form.
/// Lines within a string (the notes) are separated by "\n".
/// </summary>
internal sealed class Gd3Tag
{
    public const int TrackEnglish = 0;
    public const int TrackJapanese = 1;
    public const int GameEnglish = 2;
    public const int GameJapanese = 3;
    public const int SystemEnglish = 4;
    public const int SystemJapanese = 5;
    public const int AuthorEnglish = 6;
    public const int AuthorJapanese = 7;
    public const int ReleaseDate = 8;
    public const int Ripper = 9;
    public const int Notes = 10;
    public const int FieldCount = 11;

    private const uint Version = 0x100;
    private const int PrefixSize = 12; // "Gd3 ", version, data length

    /// <summary>A tag past this is taken for garbage rather than read.</summary>
    private const int MaxDataLength = 1 << 20;

    private readonly string[] _fields;

    public Gd3Tag() => _fields = Enumerable.Repeat("", FieldCount).ToArray();

    private Gd3Tag(string[] fields) => _fields = fields;

    /// <summary>A field ("" when empty).</summary>
    public string this[int field]
    {
        get => _fields[field];
        set => _fields[field] = value ?? "";
    }

    /// <summary>The tag at the stream's position, or null when there is none.</summary>
    public static Gd3Tag? TryRead(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[PrefixSize];
        if (stream.ReadAtLeast(prefix, PrefixSize, throwOnEndOfStream: false) < PrefixSize || !prefix[..4].SequenceEqual("Gd3 "u8))
            return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix[8..]);
        if (length > MaxDataLength) return null;

        var data = new byte[length & ~1u];
        var read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        return Parse(data.AsSpan(0, read & ~1));
    }

    /// <summary>The tag's size in bytes when <paramref name="data"/> starts with one, else null.</summary>
    public static long? EndOf(ReadOnlySpan<byte> data) =>
        data.Length >= PrefixSize && data[..4].SequenceEqual("Gd3 "u8)
            ? PrefixSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(data[8..])
            : null;

    public Gd3Tag Clone() => new((string[])_fields.Clone());

    public bool IsEmpty => _fields.All(f => f.Length == 0);

    public byte[] Encode()
    {
        var text = new StringBuilder();
        foreach (var field in _fields)
            text.Append(field.Replace("\r\n", "\n").Replace('\0', ' ')).Append('\0');
        var data = Encoding.Unicode.GetBytes(text.ToString());

        var result = new byte[PrefixSize + data.Length];
        "Gd3 "u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)data.Length);
        data.CopyTo(result, PrefixSize);
        return result;
    }

    // Eleven null-terminated strings; a truncated tag keeps what it has.
    private static Gd3Tag Parse(ReadOnlySpan<byte> data)
    {
        var text = Encoding.Unicode.GetString(data);
        var parts = text.Split('\0');
        var fields = new string[FieldCount];
        for (var i = 0; i < FieldCount; i++)
            fields[i] = i < parts.Length ? parts[i].Replace("\r\n", "\n").Trim() : "";
        return new Gd3Tag(fields);
    }
}
