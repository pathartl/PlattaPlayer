using PlattaPlayer.Codecs.Midi.Emulation;
using PlattaPlayer.Codecs.Midi.Winmm;

namespace PlattaPlayer.Codecs.Midi;

/// <summary>How a MIDI output device produces sound.</summary>
internal enum MidiDeviceKind
{
    /// <summary>MeltySynth rendering a SoundFont to PCM for the host. Supports visualizations.</summary>
    SoundFont,

    /// <summary>The operating system's default MIDI synth (e.g. Microsoft GS Wavetable Synth). Plays on its own
    /// output, so there is no PCM and no visualization.</summary>
    System,

    /// <summary>An external/hardware MIDI output port exposed by the OS. No PCM, so no visualization.</summary>
    External,

    /// <summary>
    /// A low-level emulation of a hardware sound module (gearmulator's 88emu: Roland Sound Canvas, MT-32, …)
    /// running its original firmware ROM, rendered to PCM for the host. Supports visualizations.
    /// </summary>
    Emulated
}

/// <summary>A selectable MIDI output.</summary>
/// <param name="Id">Stable identifier stored as the device setting (e.g. <c>sf2:GeneralUser.sf2</c>, <c>emu88:2</c>,
/// <c>system</c>, <c>midiout:1</c>).</param>
/// <param name="Name">Shown in Settings and the Tag Editor.</param>
/// <param name="SoundFontPath">The <c>.sf2</c> of a <see cref="MidiDeviceKind.SoundFont"/> device.</param>
/// <param name="WinmmDeviceId">The Windows <c>midiOut</c> index of a System/External device (-1 = MIDI mapper).</param>
/// <param name="EmulatorDeviceId">The 88emu model id of an <see cref="MidiDeviceKind.Emulated"/> device.</param>
internal sealed record MidiDevice(
    string Id,
    string Name,
    MidiDeviceKind Kind,
    string? SoundFontPath = null,
    int WinmmDeviceId = -1,
    int EmulatorDeviceId = -1)
{
    public const string EmulatedPrefix = "emu88:";

    /// <summary>Whether the device renders PCM for the host (and so keeps visualizations).</summary>
    public bool RendersPcm => Kind is MidiDeviceKind.SoundFont or MidiDeviceKind.Emulated;
}

/// <summary>
/// The MIDI devices: the emulated sound modules whose ROMs are in the ROMs folder, one SoundFont device per
/// <c>.sf2</c> in the SoundFonts folder, then the OS/hardware ports (Windows only). Re-scanned on each call so
/// newly dropped files appear.
/// </summary>
internal sealed class MidiDevices
{
    private readonly string? _soundFontsDirectory;
    private readonly Emu88Devices? _emulated;

    public MidiDevices(string? soundFontsDirectory, string? romsDirectory)
    {
        _soundFontsDirectory = soundFontsDirectory;
        _emulated = romsDirectory is null ? null : new Emu88Devices(romsDirectory);
    }

    public string? SoundFontsDirectory => _soundFontsDirectory;

    /// <summary>Changes whenever the ROM folder's contents do.</summary>
    public string? RomSnapshot => _emulated?.Snapshot;

    public IReadOnlyList<MidiDevice> GetDevices()
    {
        var devices = new List<MidiDevice>();
        try
        {
            if (_emulated is not null) devices.AddRange(_emulated.GetDevices());
        }
        catch
        {
            // The emulator failed to load: offer the other devices.
        }
        devices.AddRange(SoundFontDevices());
        if (OperatingSystem.IsWindows()) devices.AddRange(WinmmMidiDevices.GetDevices());
        return devices;
    }

    /// <summary>
    /// The device for <paramref name="selectedId"/> when it still exists, else the default: an emulated module
    /// (the most faithful, and it keeps visualizations), then a SoundFont device (also keeps visualizations),
    /// then the OS system synth, else whatever is first. Null only when there is no device at all.
    /// </summary>
    public MidiDevice? Resolve(string? selectedId) => Resolve(GetDevices(), selectedId);

    public static MidiDevice? Resolve(IReadOnlyList<MidiDevice> devices, string? selectedId)
    {
        if (devices.Count == 0) return null;

        // A device that vanished (its .sf2 deleted, a port unplugged) falls through to the default.
        if (!string.IsNullOrEmpty(selectedId) && devices.FirstOrDefault(d => d.Id == selectedId) is { } match)
            return match;

        return devices.FirstOrDefault(d => d.Kind == MidiDeviceKind.Emulated)
               ?? devices.FirstOrDefault(d => d.Kind == MidiDeviceKind.SoundFont)
               ?? devices.FirstOrDefault(d => d.Kind == MidiDeviceKind.System)
               ?? devices[0];
    }

    /// <summary>
    /// Resolves a MIDI file's <c>soundfont=</c> tag to an absolute <c>.sf2</c> path: first as a sidecar relative
    /// to the MIDI's own folder (kept inside that subtree, since it comes from file content), then by file name
    /// in the SoundFonts folder, with or without the <c>.sf2</c> extension. Null when it can't be found.
    /// </summary>
    public string? FindSoundFont(string midiPath, string name)
    {
        var dir = Path.GetDirectoryName(midiPath);
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                var sidecar = Path.GetFullPath(Path.Combine(dir, name));
                var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                if (sidecar.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(sidecar))
                    return sidecar;
            }
            catch
            {
                // Malformed path in the tag: try the SoundFonts folder.
            }
        }

        if (!string.IsNullOrEmpty(_soundFontsDirectory))
        {
            foreach (var candidate in new[] { name, name + ".sf2" })
            {
                try
                {
                    var p = Path.Combine(_soundFontsDirectory, candidate);
                    if (File.Exists(p))
                        return p;
                }
                catch
                {
                    // Not a usable file name.
                }
            }
        }

        return null;
    }

    private IEnumerable<MidiDevice> SoundFontDevices()
    {
        if (string.IsNullOrEmpty(_soundFontsDirectory)) yield break;

        string[] files;
        try
        {
            files = Directory.GetFiles(_soundFontsDirectory, "*.sf2");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            files = Array.Empty<string>();
        }

        foreach (var path in files)
        {
            yield return new MidiDevice(
                Id: "sf2:" + Path.GetFileName(path),
                Name: "SoundFont · " + Path.GetFileNameWithoutExtension(path),
                Kind: MidiDeviceKind.SoundFont,
                SoundFontPath: path);
        }
    }
}
