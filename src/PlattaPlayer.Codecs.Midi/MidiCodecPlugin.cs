using System.Diagnostics;
using System.Globalization;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Codecs.Midi.Emulation;
using PlattaPlayer.Codecs.Midi.SoundFont;
using PlattaPlayer.Codecs.Midi.Winmm;

namespace PlattaPlayer.Codecs.Midi;

/// <summary>
/// MIDI files (.mid, .midi, .kar, .rmi), played on the device chosen in Settings: an emulated Roland sound
/// module (gearmulator's 88emu, from the user's ROM dumps), a SoundFont rendered by MeltySynth, or a Windows
/// MIDI port. The first two render PCM for the host (so they keep visualizations); a MIDI port plays the file
/// itself (<see cref="ICodecDirectOutput"/>).
/// <para>
/// MIDI has no standard tags. Track-level fields live in PlattaPlayer's own block embedded in the file (see
/// <see cref="MidiMetadataReader"/>); album-level fields live in an M3U album file beside the files
/// (<see cref="ICodecAlbumFiles"/>). Reading, a field comes from the embedded block, else the M3U, else (in
/// the host) the file and folder names. A file's <c>soundfont=</c> tag picks its own SoundFont or emulated
/// module over the device setting.
/// </para>
/// </summary>
public sealed class MidiCodecPlugin : ICodecPlugin, ICodecSettings, ICodecAlbumFiles
{
    /// <summary>The device setting: a <see cref="MidiDevice.Id"/>.</summary>
    public const string DeviceSetting = "device";

    /// <summary>Format-specific tag: the SoundFont or emulated module the file plays with.</summary>
    public const string SoundFontKey = "soundfont";

    private readonly M3uSidecarIndex _m3u = new();
    private ICodecHost? _host;
    private MidiDevices _devices = new(null, null);
    private IReadOnlyList<CodecTagField>? _fields;
    private IReadOnlyList<CodecSetting>? _settings;

    // The booted emulated module, kept across tracks (booting takes seconds) and replaced only when another
    // model is wanted. A model that failed to boot isn't retried until the ROM folder changes.
    private readonly Lock _synthGate = new();
    private Emu88Synth? _synth;
    private (int Model, string? Roms)? _failedBoot;

    public string Id => "midi";
    public string DisplayName => "MIDI";
    public string FormatName => "MIDI";
    public IReadOnlyCollection<string> Extensions { get; } = [".mid", ".midi", ".kar", ".rmi"];

    public void Initialize(ICodecHost host)
    {
        _host = host;
        _devices = new MidiDevices(host.UserFolder("SoundFonts"), host.UserFolder("Roms"));
        _settings = null;
    }

    public IReadOnlyList<CodecTagField> TagFields => _fields ??=
    [
        new(CodecTagKeys.Title, "Title"),
        new(CodecTagKeys.Artist, "Artist"),
        new(CodecTagKeys.Album, "Album"),
        new(CodecTagKeys.AlbumArtist, "Album artist"),
        new(CodecTagKeys.Genre, "Genre"),
        new(CodecTagKeys.Year, "Year", CodecTagFieldKind.Number),
        new(CodecTagKeys.Track, "Track", CodecTagFieldKind.Number),
        new(CodecTagKeys.Disc, "Disc", CodecTagFieldKind.Number),
        new(SoundFontKey, "SoundFont / synthesizer", CodecTagFieldKind.Choice,
            Hint: "Plays this MIDI with a SoundFont from the SoundFonts folder or an emulated sound module, instead of the device chosen in Settings.",
            Choices: SoundFontChoices),
    ];

    public IReadOnlyList<CodecSetting> Settings => _settings ??=
    [
        new CodecSetting
        {
            Key = DeviceSetting,
            Label = "Default MIDI device",
            Description = "Emulated devices run the original firmware of Roland sound modules (Sound Canvas, MT-32, …) and are the most faithful. Drop the module's ROM dumps into the ROMs folder, then rescan. SoundFont devices use .sf2 files from the SoundFonts folder. Both support visualizations. System/external devices play through Windows MIDI (no visualization).",
            GetChoices = () => _devices.GetDevices().Select(d => new CodecChoice(d.Id, d.Name)).ToList(),
            GetEffective = () => _devices.Resolve(_host?.GetSetting(DeviceSetting))?.Id,
            Folders = _host is null
                ? []
                : [new CodecFolder("ROMs folder", _host.UserFolder("Roms")), new CodecFolder("SoundFonts folder", _host.UserFolder("SoundFonts"))],
        },
    ];

    public CodecFileInfo? ReadInfo(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }

        var duration = 0.0;
        try
        {
            duration = SmfParser.Parse(data).Duration;
        }
        catch
        {
            // Unreadable / non-standard MIDI: list it without a length rather than not at all.
        }

        MidiTags embedded;
        try
        {
            embedded = MidiTags.From(MidiMetadataReader.TryParse(data));
        }
        catch
        {
            embedded = new MidiTags();
        }

        // Embedded block > M3U album file; the host falls back to the file and folder names.
        var m3u = _m3u.Find(path);
        var tags = new CodecTags
        {
            [CodecTagKeys.Title] = embedded.Title ?? m3u?.Title,
            [CodecTagKeys.Artist] = embedded.Artist ?? m3u?.Artist,
            [CodecTagKeys.Album] = embedded.Album ?? m3u?.Album,
            [CodecTagKeys.AlbumArtist] = embedded.AlbumArtist ?? m3u?.AlbumArtist,
            [CodecTagKeys.Genre] = embedded.Genre ?? m3u?.Genre,
            [CodecTagKeys.Year] = Number(embedded.Year),
            [CodecTagKeys.Track] = Number(embedded.Track ?? m3u?.TrackNo),
            [CodecTagKeys.Disc] = Number(embedded.Disc),
        };
        return new CodecFileInfo { Tags = tags, Duration = TimeSpan.FromSeconds(duration) };
    }

    public ICodecDecoder Open(string path)
    {
        var data = File.ReadAllBytes(path);
        var tag = MidiTags.From(MidiMetadataReader.TryParse(data)).SoundFont;
        var devices = _devices.GetDevices();

        // The file's soundfont= tag wins over the device setting: a SoundFont it names always plays (with
        // visualizations); an emulated module it names is used when available.
        MidiDevice? device = null;
        if (tag is not null)
        {
            if (tag.StartsWith(MidiDevice.EmulatedPrefix, StringComparison.OrdinalIgnoreCase))
                device = devices.FirstOrDefault(d => d.Kind == MidiDeviceKind.Emulated && string.Equals(d.Id, tag, StringComparison.OrdinalIgnoreCase));
            else if (_devices.FindSoundFont(path, tag) is { } font)
                return new SoundFontDecoder(path, data, font, SongSeconds(data));
        }
        device ??= MidiDevices.Resolve(devices, _host?.GetSetting(DeviceSetting));

        if (device is { Kind: MidiDeviceKind.Emulated } && TryOpenEmulated(device, path, data) is { } emulated)
            return emulated;

        if (device is { Kind: MidiDeviceKind.System or MidiDeviceKind.External } && OperatingSystem.IsWindows())
        {
            try
            {
                var (events, duration) = SmfParser.Parse(data);
                return new WinmmMidiOutput(device, events, duration);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{device.Name} unavailable for {path}, falling back to a SoundFont: {ex.Message}");
            }
        }

        // The SoundFont device, or (when the chosen device couldn't play) any SoundFont, so MIDI still sounds.
        var soundFont = device is { Kind: MidiDeviceKind.SoundFont, SoundFontPath: { } own }
            ? own
            : devices.FirstOrDefault(d => d.Kind == MidiDeviceKind.SoundFont)?.SoundFontPath;
        if (soundFont is null)
            throw new InvalidOperationException("No MIDI device can play this file: add a SoundFont (.sf2) to the SoundFonts folder or sound module ROMs to the ROMs folder.");
        return new SoundFontDecoder(path, data, soundFont, SongSeconds(data));
    }

    /// <summary>The song's length as every device plays it (and the library shows it), or null.</summary>
    private static double? SongSeconds(byte[] data)
    {
        try
        {
            return SmfParser.Parse(data).Duration is > 0 and var seconds ? seconds : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Boots (or reuses) the emulated module for <paramref name="device"/> and prepares a decoder for the file.
    /// Blocks for the emulation; null when the emulator, its ROMs or the file are unusable.
    /// </summary>
    private EmulatedMidiDecoder? TryOpenEmulated(MidiDevice device, string path, byte[] data)
    {
        try
        {
            var (events, duration) = SmfParser.Parse(data);
            if (events.Length == 0) return Decline("the file has no events");

            Emu88Synth? synth;
            lock (_synthGate)
            {
                if (_synth is not { IsOpen: true } || _synth.DeviceId != device.EmulatorDeviceId)
                {
                    var attempt = (device.EmulatorDeviceId, _devices.RomSnapshot);
                    if (_failedBoot == attempt) return Decline($"{device.Name} did not boot earlier");

                    _synth?.Dispose();
                    _synth = Emu88Synth.Open(device.EmulatorDeviceId);
                    _failedBoot = _synth is null ? attempt : null;
                }
                synth = _synth;
            }
            if (synth is null) return Decline($"{device.Name} did not boot");

            return new EmulatedMidiDecoder(synth, ModelName(device), events, duration);
        }
        catch (Exception ex)
        {
            return Decline(ex.ToString());
        }

        EmulatedMidiDecoder? Decline(string reason)
        {
            Debug.WriteLine($"Emulated MIDI unavailable for {path}, falling back: {reason}");
            return null;
        }
    }

    /// <summary>The model part of an emulated device's name ("Emulated · SC-55mkII" → "SC-55mkII").</summary>
    private static string ModelName(MidiDevice device)
    {
        const string prefix = "Emulated · ";
        return device.Name.StartsWith(prefix, StringComparison.Ordinal) ? device.Name[prefix.Length..] : device.Name;
    }

    public string? FindCover(string path) => MidiCoverResolver.Resolve(path, _m3u);

    public CodecTags ReadTags(string path)
    {
        var tags = MidiTags.Read(path);
        return new CodecTags
        {
            [CodecTagKeys.Title] = tags.Title,
            [CodecTagKeys.Artist] = tags.Artist,
            [CodecTagKeys.Album] = tags.Album,
            [CodecTagKeys.AlbumArtist] = tags.AlbumArtist,
            [CodecTagKeys.Genre] = tags.Genre,
            [CodecTagKeys.Year] = Number(tags.Year),
            [CodecTagKeys.Track] = Number(tags.Track),
            [CodecTagKeys.Disc] = Number(tags.Disc),
            [SoundFontKey] = AsChoice(tags.SoundFont),
        };
    }

    public void WriteTags(string path, CodecTags tags)
    {
        // Starts from the file's block so the fields not edited here (the cover) survive.
        var block = MidiTags.Read(path);
        block.Title = tags[CodecTagKeys.Title];
        block.Artist = tags[CodecTagKeys.Artist];
        block.Album = tags[CodecTagKeys.Album];
        block.AlbumArtist = tags[CodecTagKeys.AlbumArtist];
        block.Genre = tags[CodecTagKeys.Genre];
        block.Year = tags.GetNumber(CodecTagKeys.Year);
        block.Track = tags.GetNumber(CodecTagKeys.Track);
        block.Disc = tags.GetNumber(CodecTagKeys.Disc);
        block.SoundFont = tags[SoundFontKey];
        MidiTagWriter.Write(path, block);
    }

    /// <summary>What the soundfont= field can be: the device setting (blank), each SoundFont in the SoundFonts
    /// folder, and each emulated sound module whose ROMs are present.</summary>
    private IReadOnlyList<CodecChoice> SoundFontChoices()
    {
        var choices = new List<CodecChoice> { new(string.Empty, "Device setting (default)") };
        foreach (var device in _devices.GetDevices())
        {
            if (device is { Kind: MidiDeviceKind.SoundFont, SoundFontPath: { } path })
                choices.Add(new CodecChoice(Path.GetFileName(path), device.Name));
            else if (device.Kind == MidiDeviceKind.Emulated)
                choices.Add(new CodecChoice(device.Id, device.Name));
        }
        return choices;
    }

    /// <summary>A SoundFont may be tagged without its .sf2 extension; shown as the file name it stands for.</summary>
    private string? AsChoice(string? value)
    {
        if (value is null || value.StartsWith(MidiDevice.EmulatedPrefix, StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase) || _devices.SoundFontsDirectory is not { } dir)
            return value;
        try
        {
            return File.Exists(Path.Combine(dir, value + ".sf2")) ? value + ".sf2" : value;
        }
        catch
        {
            return value;
        }
    }

    // --- Album files: M3U sidecars --------------------------------------------------------------------

    public CodecAlbumEntry? FindAlbumEntry(string path) => _m3u.Find(path);

    public IReadOnlyList<string> WriteAlbum(IReadOnlyCollection<string> paths, CodecAlbumChanges changes)
    {
        if (changes.IsEmpty || paths.Count == 0) return [];
        var written = M3uSidecarWriter.Apply(_m3u, paths, changes);

        // An embedded copy of a changed field would override the album file: drop it.
        foreach (var path in paths)
        {
            var block = MidiTags.Read(path);
            var dirty = false;
            if (changes.SetAlbum && block.Album is not null) { block.Album = null; dirty = true; }
            if (changes.SetAlbumArtist && block.AlbumArtist is not null) { block.AlbumArtist = null; dirty = true; }
            if (changes.SetGenre && block.Genre is not null) { block.Genre = null; dirty = true; }
            if (changes.CoverSourcePath is not null && block.Cover is not null) { block.Cover = null; dirty = true; }
            if (dirty) MidiTagWriter.Write(path, block);
        }
        return written;
    }

    public IReadOnlyDictionary<string, int> WriteTrackOrder(IReadOnlyList<string> pathsInOrder, bool save) =>
        M3uSidecarWriter.Reorder(_m3u, pathsInOrder, save);

    private static string? Number(int? n) => n?.ToString(CultureInfo.InvariantCulture);
}
