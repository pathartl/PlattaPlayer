using System.Runtime.InteropServices;

namespace PlattaPlayer.Codecs.Midi.Emulation;

/// <summary>
/// Process-wide state of the emulator library: whether <c>emu88.dll</c> loads at all, and the ROM search
/// path (which 88emu keeps per process, not per context). All calls are serialized here.
/// </summary>
internal static class Emu88Library
{
    private static readonly Lock Gate = new();
    private static bool? _available;
    private static string? _romPath;

    /// <summary>True once <c>emu88.dll</c> has loaded; false when it is missing or fails to load.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                _available ??= Probe();
                return _available.Value;
            }
        }
    }

    /// <summary>Points the ROM search at <paramref name="romPath"/> (with subfolders) and sweeps it.</summary>
    public static bool UseRomPath(string romPath)
    {
        if (!IsAvailable) return false;
        lock (Gate)
        {
            if (_romPath == romPath) return true;
            if (Emu88Native.SetRomPath(romPath) != Emu88Native.RcOk) return false;
            _romPath = romPath;
            return true;
        }
    }

    /// <summary>Sweeps the ROM path again, e.g. after the user dropped in a dump.</summary>
    public static void Rescan()
    {
        if (!IsAvailable) return;
        lock (Gate) Emu88Native.RescanRoms();
    }

    /// <summary>Every model the library knows, in its presentation order, with whether its ROMs were found.</summary>
    public static IReadOnlyList<(int Id, string Name, bool Available)> GetModels()
    {
        if (!IsAvailable) return Array.Empty<(int, string, bool)>();
        lock (Gate)
        {
            var count = Emu88Native.GetDeviceCount();
            var models = new List<(int, string, bool)>(count);
            for (var i = 0; i < count; i++)
            {
                var id = Emu88Native.GetDeviceId(i);
                var name = Emu88Native.PtrToString(Emu88Native.GetDeviceName(id)) ?? $"Device {id}";
                models.Add((id, name, Emu88Native.IsDeviceAvailable(id) != 0));
            }
            return models;
        }
    }

    private static bool Probe()
    {
        try
        {
            if (!NativeLibrary.TryLoad("emu88", typeof(Emu88Library).Assembly, null, out _))
                return false;
            _ = Emu88Native.GetLibraryVersionString();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The emulated MIDI devices over 88emu: one device per Sound Canvas / MT-32 / CM model whose ROMs are in the
/// ROM folder. The folder is only re-swept when its contents change, because 88emu identifies images by
/// hashing them and the device list is consulted on every MIDI track load.
/// </summary>
internal sealed class Emu88Devices
{
    // GS/GM modules first (most MIDI files are GM/GS), the period-defining SC-88Pro ahead of the rest;
    // models 88emu marks experimental, and the MT-32 family (whose instrument map is not GM), after them.
    // Models not named here follow in the library's own order.
    private static readonly int[] Preference =
    [
        2,  // SC-88Pro
        3,  // SC-8850
        1,  // SC-88VL
        0,  // SC-88
        4,  // SC-55mkII
        5,  // SC-55
        13, // SC-8820 (experimental)
    ];

    private readonly string _romsDirectory;
    private readonly Lock _gate = new();
    private string? _snapshot;
    private IReadOnlyList<MidiDevice> _devices = Array.Empty<MidiDevice>();

    public Emu88Devices(string romsDirectory) => _romsDirectory = romsDirectory;

    /// <summary>Changes whenever the ROM folder's contents do (so a failed boot is worth retrying).</summary>
    public string? Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public IReadOnlyList<MidiDevice> GetDevices()
    {
        if (!Emu88Library.UseRomPath(_romsDirectory))
            return Array.Empty<MidiDevice>();

        lock (_gate)
        {
            var snapshot = FolderSnapshot();
            if (snapshot == _snapshot) return _devices;

            if (_snapshot is not null) Emu88Library.Rescan(); // the first UseRomPath already swept
            _snapshot = snapshot;

            _devices = Emu88Library.GetModels()
                .Where(m => m.Available)
                .OrderBy(m => Array.IndexOf(Preference, m.Id) is var rank and >= 0 ? rank : int.MaxValue)
                .Select(m => new MidiDevice(
                    Id: MidiDevice.EmulatedPrefix + m.Id,
                    Name: "Emulated · " + m.Name,
                    Kind: MidiDeviceKind.Emulated,
                    EmulatorDeviceId: m.Id))
                .ToArray();
            return _devices;
        }
    }

    /// <summary>A cheap fingerprint of the ROM folder: every file's path, size and write time.</summary>
    private string FolderSnapshot()
    {
        try
        {
            var files = new DirectoryInfo(_romsDirectory).EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}")
                .Order(StringComparer.Ordinal);
            return string.Join('\n', files);
        }
        catch
        {
            return string.Empty;
        }
    }
}
