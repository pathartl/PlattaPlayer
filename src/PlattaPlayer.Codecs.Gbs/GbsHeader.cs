using System.Buffers.Binary;
using System.Text;

namespace PlattaPlayer.Codecs.Gbs;

/// <summary>
/// The 112-byte header of a GBS file (version 1): "GBS" and the version, the song count and first song (1-based),
/// the load, init and play addresses, the initial stack pointer, the timer setup (TMA, TAC) and three 32-byte
/// text fields: the game title, the author and the copyright line. The text is ASCII (Latin-1 in practice) and
/// zero-padded; a field that fills all 32 bytes has no terminator.
/// </summary>
internal sealed class GbsHeader
{
    public const int Size = 0x70;
    public const int TextLength = 32;
    private const int TitleOffset = 0x10, AuthorOffset = 0x30, CopyrightOffset = 0x50;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    public int SongCount { get; private init; }

    /// <summary>The song a player starts with, 0-based.</summary>
    public int FirstSong { get; private init; }

    public ushort LoadAddress { get; private init; }
    public ushort InitAddress { get; private init; }
    public ushort PlayAddress { get; private init; }
    public ushort StackPointer { get; private init; }
    public byte Tma { get; private init; }
    public byte Tac { get; private init; }
    public string? Title { get; private init; }
    public string? Author { get; private init; }
    public string? Copyright { get; private init; }

    /// <summary>Parses the header; null when the data isn't a GBS SameBoy would play (wrong signature or
    /// version, or a load address that overlaps its player routine or lies outside ROM).</summary>
    public static GbsHeader? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size || !data[..4].SequenceEqual("GBS\x01"u8)) return null;
        var load = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (load != 0 && load is < 0x61 + 13 or >= 0x8000) return null;
        return new GbsHeader
        {
            SongCount = data[4],
            FirstSong = (byte)(data[5] - 1),
            LoadAddress = load,
            InitAddress = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            PlayAddress = BinaryPrimitives.ReadUInt16LittleEndian(data[0xA..]),
            StackPointer = BinaryPrimitives.ReadUInt16LittleEndian(data[0xC..]),
            Tma = data[0xE],
            Tac = data[0xF],
            Title = ReadText(data.Slice(TitleOffset, TextLength)),
            Author = ReadText(data.Slice(AuthorOffset, TextLength)),
            Copyright = ReadText(data.Slice(CopyrightOffset, TextLength)),
        };
    }

    /// <summary>Rewrites the title field in <paramref name="file"/> (Latin-1, truncated to 32 bytes, zero padded;
    /// characters Latin-1 lacks become '?').</summary>
    public static void WriteTitle(Span<byte> file, string? text) => WriteField(file.Slice(TitleOffset, TextLength), text);

    public static void WriteAuthor(Span<byte> file, string? text) => WriteField(file.Slice(AuthorOffset, TextLength), text);

    public static void WriteCopyright(Span<byte> file, string? text) => WriteField(file.Slice(CopyrightOffset, TextLength), text);

    private static string? ReadText(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        var text = Latin1.GetString(end < 0 ? field : field[..end]).Trim();
        // Unset fields are often filled with "?" or "<?>" by rippers.
        return text.Length == 0 || text is "?" or "<?>" ? null : text;
    }

    private static void WriteField(Span<byte> field, string? text)
    {
        field.Clear();
        if (string.IsNullOrWhiteSpace(text)) return;
        var chars = text.Trim().Select(c => c <= 0xFF && !char.IsControl(c) ? c : '?').ToArray();
        var length = Math.Min(chars.Length, TextLength);
        for (var i = 0; i < length; i++) field[i] = (byte)chars[i];
    }
}
