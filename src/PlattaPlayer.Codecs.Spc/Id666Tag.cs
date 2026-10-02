using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PlattaPlayer.Codecs.Spc;

/// <summary>
/// An SPC file's ID666 tag (in the header) together with its extended "xid6" tag (appended after the
/// snapshot). ID666 comes in two layouts — text and binary — that differ only from the dump date on and
/// are told apart heuristically; xid6 adds longer strings, soundtrack and publisher fields, and timing in
/// 1/64000 s ticks. Reading prefers an xid6 value over its ID666 counterpart. Writing always produces the
/// text layout (the one every player reads), stores each string in ID666 as far as it fits and in xid6 when
/// it doesn't, and keeps xid6 sub-chunks it doesn't edit.
/// </summary>
internal sealed class Id666Tag
{
    // ID666 header offsets shared by both layouts.
    private const int HasTagOffset = 0x23;        // 26 = has ID666, 27 = none
    private const int VersionOffset = 0x24;
    private const int SongOffset = 0x2e, SongLength = 32;
    private const int GameOffset = 0x4e, GameLength = 32;
    private const int DumperOffset = 0x6e, DumperLength = 16;
    private const int CommentOffset = 0x7e, CommentLength = 32;
    private const int DateOffset = 0x9e;

    // Text layout.
    private const int TextDateLength = 11;
    private const int TextLengthOffset = 0xa9, TextLengthLength = 3;
    private const int TextFadeOffset = 0xac, TextFadeLength = 5;
    private const int TextArtistOffset = 0xb1, ArtistLength = 32;
    private const int TextMuteOffset = 0xd1;
    private const int TextEmulatorOffset = 0xd2;

    // Binary layout.
    private const int BinaryLengthOffset = 0xa9;  // 3 bytes, seconds
    private const int BinaryFadeOffset = 0xac;    // 4 bytes, milliseconds
    private const int BinaryArtistOffset = 0xb0;
    private const int BinaryMuteOffset = 0xd0;
    private const int BinaryEmulatorOffset = 0xd1;

    // xid6 sub-chunk ids.
    private const byte XSong = 0x01, XGame = 0x02, XArtist = 0x03, XDumper = 0x04, XDate = 0x05, XEmulator = 0x06,
        XComment = 0x07, XOstTitle = 0x10, XOstDisc = 0x11, XOstTrack = 0x12, XPublisher = 0x13, XCopyright = 0x14,
        XIntro = 0x30, XLoop = 0x31, XEnd = 0x32, XFade = 0x33, XMuted = 0x34, XLoopCount = 0x35, XAmp = 0x36;

    // xid6 sub-chunk types.
    private const byte TypeData = 0, TypeString = 1, TypeInteger = 4;

    /// <summary>Timing ticks per second in xid6.</summary>
    public const int TicksPerSecond = 64000;

    public string? Song { get; set; }
    public string? Game { get; set; }
    public string? Artist { get; set; }
    public string? Dumper { get; set; }
    public string? Comment { get; set; }
    public string? OstTitle { get; set; }
    public string? Publisher { get; set; }
    public int? OstDisc { get; set; }
    public int? OstTrack { get; set; }
    /// <summary>The optional letter after the soundtrack track number (e.g. the "a" of track 12a).</summary>
    public char? OstTrackSuffix { get; set; }
    public int? CopyrightYear { get; set; }
    public DateOnly? DumpDate { get; set; }

    /// <summary>The dump date as stored when it isn't a valid date (some dumpers wrote free text).</summary>
    public string? DumpDateText { get; set; }

    /// <summary>Play time before the fade, in seconds (ID666).</summary>
    public double? LengthSeconds { get; set; }

    /// <summary>Fade-out length, in seconds (ID666 milliseconds, or xid6 ticks when present).</summary>
    public double? FadeSeconds { get; set; }

    // xid6 timing: the song is intro + loop × loop count + end, more precise than the ID666 length.
    public int? IntroTicks { get; set; }
    public int? LoopTicks { get; set; }
    public int? EndTicks { get; set; }
    public int? LoopCount { get; set; }

    // Kept as found.
    private byte _channelDisables;
    private byte _emulator;

    // xid6 sub-chunks this class doesn't model (muted voices, amplification, unknown ids), written back as-is.
    private readonly List<(byte Id, byte Type, byte[] Payload, ushort Data)> _otherChunks = new();

    /// <summary>The play time before the fade: the xid6 intro/loop/end timing when present, else the ID666
    /// length. Null when the file doesn't say.</summary>
    public double? PlaySeconds
    {
        get
        {
            if (IntroTicks is not null || LoopTicks is not null || EndTicks is not null)
            {
                var ticks = (long)(IntroTicks ?? 0) + (long)(LoopTicks ?? 0) * Math.Max(1, LoopCount ?? 1) + (EndTicks ?? 0);
                if (ticks > 0) return ticks / (double)TicksPerSecond;
            }
            return LengthSeconds is > 0 ? LengthSeconds : null;
        }
    }

    // ----- Reading -------------------------------------------------------------------------------------

    public static Id666Tag Read(byte[] file)
    {
        var tag = new Id666Tag();
        if (file.Length >= SpcFile.HeaderSize && file[HasTagOffset] == 26) tag.ReadId666(file);
        if (file.Length > SpcFile.SnapshotSize + 8) tag.ReadXid6(file.AsSpan(SpcFile.SnapshotSize));
        return tag;
    }

    private void ReadId666(byte[] h)
    {
        Song = Text(h, SongOffset, SongLength);
        Game = Text(h, GameOffset, GameLength);
        Dumper = Text(h, DumperOffset, DumperLength);
        Comment = Text(h, CommentOffset, CommentLength);

        if (IsTextLayout(h))
        {
            ParseDateText(Text(h, DateOffset, TextDateLength));
            LengthSeconds = Digits(h, TextLengthOffset, TextLengthLength);
            FadeSeconds = Digits(h, TextFadeOffset, TextFadeLength) / 1000.0;
            Artist = Text(h, TextArtistOffset, ArtistLength);
            _channelDisables = h[TextMuteOffset];
            _emulator = h[TextEmulatorOffset] is >= (byte)'0' and <= (byte)'9' ? (byte)(h[TextEmulatorOffset] - '0') : h[TextEmulatorOffset];
        }
        else
        {
            int day = h[DateOffset], month = h[DateOffset + 1], year = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(DateOffset + 2));
            if (year is > 0 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
                DumpDate = new DateOnly(year, month, day);
            LengthSeconds = h[BinaryLengthOffset] | h[BinaryLengthOffset + 1] << 8 | h[BinaryLengthOffset + 2] << 16;
            FadeSeconds = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(BinaryFadeOffset)) / 1000.0;
            Artist = Text(h, BinaryArtistOffset, ArtistLength);
            _channelDisables = h[BinaryMuteOffset];
            _emulator = h[BinaryEmulatorOffset];
        }

        if (LengthSeconds is <= 0) LengthSeconds = null;
        if (FadeSeconds is < 0) FadeSeconds = null;
    }

    /// <summary>
    /// Tells the text layout from the binary one. In the text layout the length and fade are digits (or
    /// empty) and the date is digits and separators; binary values rarely look like that, and when both are
    /// empty, a printable byte where the binary layout's artist starts (0xB0) — one before the text layout's
    /// — decides it.
    /// </summary>
    internal static bool IsTextLayout(ReadOnlySpan<byte> h)
    {
        static bool Numeric(ReadOnlySpan<byte> field)
        {
            var ended = false;
            foreach (var b in field)
            {
                if (b == 0) { ended = true; continue; }
                if (ended || b is not ((>= (byte)'0' and <= (byte)'9') or (byte)' ')) return false;
            }
            return true;
        }

        static bool DateLike(ReadOnlySpan<byte> field)
        {
            foreach (var b in field)
                if (b != 0 && b is not ((>= (byte)'0' and <= (byte)'9') or (byte)'/' or (byte)'-' or (byte)'.' or (byte)' '))
                    return false;
            return true;
        }

        if (!Numeric(h.Slice(TextLengthOffset, TextLengthLength)) || !Numeric(h.Slice(TextFadeOffset, TextFadeLength))
            || !DateLike(h.Slice(DateOffset, TextDateLength)))
            return false;

        var timesEmpty = h.Slice(DateOffset, TextFadeOffset + TextFadeLength - DateOffset).IndexOfAnyExcept((byte)0) < 0;
        return !timesEmpty || h[BinaryArtistOffset] < 0x20;
    }

    private void ParseDateText(string? text)
    {
        if (text is null) return;
        string[] formats = ["MM/dd/yyyy", "M/d/yyyy", "yyyy/MM/dd", "yyyy-MM-dd", "MM-dd-yyyy", "dd.MM.yyyy", "yyyyMMdd", "MM/dd/yy", "M/d/yy"];
        if (DateOnly.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            DumpDate = date;
        else
            DumpDateText = text.Trim();
    }

    private void ReadXid6(ReadOnlySpan<byte> x)
    {
        if (!x[..4].SequenceEqual("xid6"u8)) return;
        var size = BinaryPrimitives.ReadInt32LittleEndian(x.Slice(4));
        if (size <= 0) return;
        var body = x.Slice(8, Math.Min(size, x.Length - 8));

        var pos = 0;
        while (pos + 4 <= body.Length)
        {
            var id = body[pos];
            var type = body[pos + 1];
            var data = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 2));
            pos += 4;

            byte[] payload = [];
            if (type != TypeData)
            {
                var length = data;
                if (pos + length > body.Length) break;
                payload = body.Slice(pos, length).ToArray();
                pos += (length + 3) & ~3;  // payloads are padded to 32 bits
            }

            switch (id)
            {
                case XSong when type == TypeString: Song = Decode(payload) ?? Song; break;
                case XGame when type == TypeString: Game = Decode(payload) ?? Game; break;
                case XArtist when type == TypeString: Artist = Decode(payload) ?? Artist; break;
                case XDumper when type == TypeString: Dumper = Decode(payload) ?? Dumper; break;
                case XComment when type == TypeString: Comment = Decode(payload) ?? Comment; break;
                case XOstTitle when type == TypeString: OstTitle = Decode(payload); break;
                case XPublisher when type == TypeString: Publisher = Decode(payload); break;
                case XDate when type == TypeInteger && payload.Length >= 4:
                    var ymd = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    if (DateOnly.TryParseExact(ymd.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    {
                        DumpDate = d;
                        DumpDateText = null;
                    }
                    break;
                case XEmulator when type == TypeData: _emulator = (byte)data; break;
                case XOstDisc when type == TypeData: OstDisc = (data & 0xff) is > 0 and var disc ? disc : null; break;
                case XOstTrack when type == TypeData:
                    OstTrack = data >> 8 is > 0 and var track ? track : null;
                    OstTrackSuffix = (data & 0xff) is >= 0x20 and < 0x7f and var c ? (char)c : null;
                    break;
                case XCopyright when type == TypeData: CopyrightYear = data > 0 ? data : null; break;
                case XIntro when type == TypeInteger && payload.Length >= 4: IntroTicks = BinaryPrimitives.ReadInt32LittleEndian(payload); break;
                case XLoop when type == TypeInteger && payload.Length >= 4: LoopTicks = BinaryPrimitives.ReadInt32LittleEndian(payload); break;
                case XEnd when type == TypeInteger && payload.Length >= 4: EndTicks = BinaryPrimitives.ReadInt32LittleEndian(payload); break;
                case XFade when type == TypeInteger && payload.Length >= 4:
                    FadeSeconds = BinaryPrimitives.ReadInt32LittleEndian(payload) / (double)TicksPerSecond;
                    break;
                case XLoopCount when type == TypeData: LoopCount = data & 0xff; break;
                default: _otherChunks.Add((id, type, payload, data)); break;
            }
        }
    }

    // ----- Writing -------------------------------------------------------------------------------------

    /// <summary>Returns the file with this tag written into it (text-layout ID666 plus xid6 as needed).</summary>
    public byte[] WriteTo(byte[] file)
    {
        if (file.Length < SpcFile.SnapshotSize) throw new InvalidDataException("Not a complete SPC file.");

        var x = new List<(byte Id, byte Type, byte[] Payload, ushort Data)>();
        var h = file.AsSpan(0, SpcFile.HeaderSize).ToArray();
        h[HasTagOffset] = 26;
        h[VersionOffset] = 30;
        h.AsSpan(SongOffset, SpcFile.HeaderSize - SongOffset).Clear();

        WriteString(h, SongOffset, SongLength, Song, XSong, x);
        WriteString(h, GameOffset, GameLength, Game, XGame, x);
        WriteString(h, DumperOffset, DumperLength, Dumper, XDumper, x);
        WriteString(h, CommentOffset, CommentLength, Comment, XComment, x);
        WriteString(h, TextArtistOffset, ArtistLength, Artist, XArtist, x);

        if (DumpDate is { } date)
        {
            Ascii(h, DateOffset, TextDateLength, date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
        }
        else if (!string.IsNullOrWhiteSpace(DumpDateText))
        {
            Ascii(h, DateOffset, TextDateLength, DumpDateText);
        }

        // Length: whole seconds in the header (at most 999). The xid6 intro/loop/end timing is kept while
        // it's untouched (editing the length clears it); an edited length that is fractional or longer than
        // the header holds goes into xid6 as the intro length.
        if (PlaySeconds is { } play)
            Ascii(h, TextLengthOffset, TextLengthLength, Math.Min(999, (int)Math.Round(play, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));
        if (IntroTicks is not null || LoopTicks is not null || EndTicks is not null)
        {
            if (IntroTicks is { } intro) x.Add(Integer(XIntro, intro));
            if (LoopTicks is { } loop) x.Add(Integer(XLoop, loop));
            if (EndTicks is { } end) x.Add(Integer(XEnd, end));
            if (LoopCount is { } count) x.Add((XLoopCount, TypeData, [], (ushort)count));
        }
        else if (LengthSeconds is > 0 and var length && (length > 999 || Math.Abs(length - Math.Round(length)) > 0.0005))
        {
            x.Add(Integer(XIntro, (int)Math.Round(length * TicksPerSecond)));
        }

        // Fade: milliseconds in the header (at most 99999), ticks in xid6 beyond that.
        if (FadeSeconds is >= 0 and var fade)
        {
            var ms = (int)Math.Round(fade * 1000);
            Ascii(h, TextFadeOffset, TextFadeLength, Math.Min(99999, ms).ToString(CultureInfo.InvariantCulture));
            if (ms > 99999 || Math.Abs(fade * 1000 - ms) > 0.01) x.Add(Integer(XFade, (int)Math.Round(fade * TicksPerSecond)));
        }

        h[TextMuteOffset] = _channelDisables;
        h[TextEmulatorOffset] = _emulator <= 9 ? (byte)('0' + _emulator) : _emulator;

        // xid6-only fields.
        if (!string.IsNullOrWhiteSpace(OstTitle)) x.Add(StringChunk(XOstTitle, OstTitle));
        if (OstDisc is > 0 and var disc) x.Add((XOstDisc, TypeData, [], (ushort)Math.Min(disc, 255)));
        if (OstTrack is > 0 and var track)
            x.Add((XOstTrack, TypeData, [], (ushort)(Math.Min(track, 255) << 8 | (OstTrackSuffix is { } c and > ' ' and < '\x7f' ? c : 0))));
        if (!string.IsNullOrWhiteSpace(Publisher)) x.Add(StringChunk(XPublisher, Publisher));
        if (CopyrightYear is > 0 and var year) x.Add((XCopyright, TypeData, [], (ushort)Math.Min(year, ushort.MaxValue)));
        if (DumpDate is { } dumped)
            x.Add(Integer(XDate, int.Parse(dumped.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)));
        if (x.Count > 0 && _emulator != 0) x.Add((XEmulator, TypeData, [], _emulator));
        x.AddRange(_otherChunks);

        using var output = new MemoryStream(SpcFile.SnapshotSize + 256);
        output.Write(h);
        output.Write(file, SpcFile.HeaderSize, SpcFile.SnapshotSize - SpcFile.HeaderSize);
        if (x.Count > 0) WriteXid6(output, x);
        return output.ToArray();
    }

    private static void WriteXid6(Stream output, List<(byte Id, byte Type, byte[] Payload, ushort Data)> chunks)
    {
        using var body = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        foreach (var (id, type, payload, data) in chunks.OrderBy(c => c.Id))
        {
            header[0] = id;
            header[1] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], type == TypeData ? data : (ushort)payload.Length);
            body.Write(header);
            if (type == TypeData) continue;
            body.Write(payload);
            for (var pad = payload.Length; (pad & 3) != 0; pad++) body.WriteByte(0);
        }

        output.Write("xid6"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, (int)body.Length);
        output.Write(size);
        body.Position = 0;
        body.CopyTo(output);
    }

    private static (byte, byte, byte[], ushort) Integer(byte id, int value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, value);
        return (id, TypeInteger, payload, 4);
    }

    private static (byte, byte, byte[], ushort) StringChunk(byte id, string value)
    {
        var bytes = Encode(value);
        if (bytes.Length > 255) bytes = bytes[..255];
        var payload = new byte[bytes.Length + 1];  // null-terminated
        bytes.CopyTo(payload, 0);
        return (id, TypeString, payload, (ushort)payload.Length);
    }

    /// <summary>Writes a string into its ID666 field, and into xid6 as well when it doesn't fit.</summary>
    private static void WriteString(byte[] h, int offset, int length, string? value, byte xid6Id,
        List<(byte, byte, byte[], ushort)> x)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var bytes = Encode(value.Trim());
        if (bytes.Length > length)
        {
            x.Add(StringChunk(xid6Id, value.Trim()));
            bytes = TruncateUtf8Safe(bytes, length);
        }
        bytes.CopyTo(h, offset);
    }

    private static void Ascii(byte[] h, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(length, bytes.Length)).CopyTo(h.AsSpan(offset));
    }

    // ----- Text encoding -------------------------------------------------------------------------------

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Strings are ASCII in most dumps, Latin-1 or Shift-JIS in some; read valid UTF-8 as such and
    /// anything else as Latin-1 so no byte is lost.</summary>
    private static string? Decode(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Latin1.GetString(bytes);
        }
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? Text(byte[] h, int offset, int length) => Decode(h.AsSpan(offset, length));

    /// <summary>Latin-1 when the text fits it (what other players expect), UTF-8 otherwise.</summary>
    private static byte[] Encode(string value) =>
        value.All(c => c <= '\xff') ? Latin1.GetBytes(value) : Encoding.UTF8.GetBytes(value);

    // Cut at a byte limit without splitting a UTF-8 sequence.
    private static byte[] TruncateUtf8Safe(byte[] bytes, int length)
    {
        var cut = length;
        while (cut > 0 && cut < bytes.Length && (bytes[cut] & 0xc0) == 0x80) cut--;
        return bytes[..cut];
    }

    private static double? Digits(byte[] h, int offset, int length)
    {
        var text = Encoding.ASCII.GetString(h, offset, length).TrimEnd('\0').Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
