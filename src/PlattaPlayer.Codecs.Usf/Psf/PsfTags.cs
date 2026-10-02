using System.Text;

namespace PlattaPlayer.Codecs.Usf.Psf;

/// <summary>
/// A PSF tag section: "name=value" lines, in file order. Per the PSF spec names are case-insensitive,
/// whitespace around names and values is ignored, and a name repeated on consecutive lines continues a
/// multi-line value. Names starting with "_" are for the player (e.g. "_lib", "_enablecompare").
/// <para>The text is UTF-8 when the "utf8" tag is set (or it decodes as UTF-8), else the ripper's system
/// code page, read here as Latin-1. It is always written as UTF-8, with "utf8=1".</para>
/// </summary>
internal sealed class PsfTags
{
    public const string Utf8Tag = "utf8";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly List<(string Name, string Value)> _tags = [];

    public IEnumerable<(string Name, string Value)> All => _tags;

    /// <summary>The value of <paramref name="name"/> (lines joined with '\n'), or null when it isn't set.</summary>
    public string? this[string name]
    {
        get
        {
            var index = IndexOf(name);
            return index < 0 ? null : _tags[index].Value;
        }
    }

    public static PsfTags Parse(ReadOnlySpan<byte> section)
    {
        var end = section.IndexOf((byte)0);
        if (end >= 0) section = section[..end];

        string text;
        try
        {
            text = StrictUtf8.GetString(section);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(section);
        }

        var tags = new PsfTags();
        string? previous = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var equals = rawLine.IndexOf('=');
            if (equals < 0)
            {
                previous = null;
                continue;
            }

            var name = Trim(rawLine.AsSpan(0, equals));
            var value = Trim(rawLine.AsSpan(equals + 1));
            if (name.Length == 0)
            {
                previous = null;
                continue;
            }

            var index = tags.IndexOf(name);
            if (index >= 0 && string.Equals(previous, name, StringComparison.OrdinalIgnoreCase))
                tags._tags[index] = (tags._tags[index].Name, tags._tags[index].Value + "\n" + value);
            else if (index >= 0)
                tags._tags[index] = (tags._tags[index].Name, value); // A later line wins, as in psflib.
            else
                tags._tags.Add((name, value));
            previous = name;
        }
        return tags;
    }

    /// <summary>Sets <paramref name="name"/> in place (or appends it); a null or blank value removes it.</summary>
    public void Set(string name, string? value)
    {
        var index = IndexOf(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (index >= 0) _tags.RemoveAt(index);
            return;
        }

        value = value.Trim().Replace("\r\n", "\n");
        if (index >= 0) _tags[index] = (_tags[index].Name, value);
        else _tags.Add((name, value));
    }

    public void Remove(string name) => Set(name, null);

    /// <summary>The section's text (without the "[TAG]" marker): one line per value line.</summary>
    public string Format()
    {
        var text = new StringBuilder();
        foreach (var (name, value) in _tags)
            foreach (var line in value.Split('\n'))
                text.Append(name).Append('=').Append(line.TrimEnd('\r')).Append('\n');
        return text.ToString();
    }

    private int IndexOf(string name) =>
        _tags.FindIndex(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    // The spec treats every byte up to 0x20 as whitespace.
    private static string Trim(ReadOnlySpan<char> text)
    {
        var start = 0;
        while (start < text.Length && text[start] <= ' ') start++;
        var end = text.Length;
        while (end > start && text[end - 1] <= ' ') end--;
        return text[start..end].ToString();
    }
}
