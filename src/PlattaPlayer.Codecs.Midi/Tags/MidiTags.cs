namespace PlattaPlayer.Codecs.Midi;

/// <summary>
/// The editable metadata of a MIDI file, mapped 1:1 onto PlattaPlayer's embedded <c>PLPL\n</c> tag
/// keys (see <see cref="MidiMetadataReader"/> / <see cref="MidiTagWriter"/>). Every field is optional;
/// null/empty fields are simply omitted from the written block. <see cref="Cover"/> is a path to a
/// sidecar image relative to the MIDI file (art is never embedded in the MIDI itself).
/// </summary>
internal sealed class MidiTags
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
    public int? Track { get; set; }
    public int? Disc { get; set; }
    public string? Cover { get; set; }

    /// <summary>Optional name of the SoundFont to render this MIDI with: either a <c>.sf2</c> in the
    /// SoundFonts drop-in folder (by file name, with or without extension) or a sidecar path relative
    /// to the MIDI file — or an emulated sound module's device id (<c>emu88:&lt;model&gt;</c>). Overrides the
    /// global MIDI device when it resolves; null uses the device setting.</summary>
    public string? SoundFont { get; set; }

    /// <summary>Reads the file's embedded block; empty when it has none (or can't be read).</summary>
    public static MidiTags Read(string path) => From(MidiMetadataReader.TryRead(path));

    /// <summary>The tags of an embedded block as read by <see cref="MidiMetadataReader"/>; empty for null.</summary>
    public static MidiTags From(IReadOnlyDictionary<string, string>? dict)
    {
        if (dict is null)
            return new MidiTags();

        return new MidiTags
        {
            Title = Get(dict, "title"),
            Artist = Get(dict, "artist"),
            AlbumArtist = Get(dict, "albumartist"),
            Album = Get(dict, "album"),
            Genre = Get(dict, "genre"),
            Year = GetInt(dict, "year"),
            Track = GetInt(dict, "track"),
            Disc = GetInt(dict, "disc"),
            Cover = Get(dict, "cover"),
            SoundFont = Get(dict, "soundfont"),
        };
    }

    private static string? Get(IReadOnlyDictionary<string, string> dict, string key) =>
        dict.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static int? GetInt(IReadOnlyDictionary<string, string> dict, string key) =>
        dict.TryGetValue(key, out var v) && int.TryParse(v.Trim(), out var n) && n > 0 ? n : null;
}
