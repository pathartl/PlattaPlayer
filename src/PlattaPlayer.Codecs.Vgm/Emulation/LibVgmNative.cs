using System.Runtime.InteropServices;

namespace PlattaPlayer.Codecs.Vgm.Emulation;

/// <summary>
/// P/Invoke surface of <c>ppvgm.dll</c>: libvgm's VGM player behind the small <c>pp_vgm_</c> interface of
/// <c>native/libvgm/ppvgm.cpp</c>, built by <c>native/libvgm/build.ps1</c> and staged beside the plugin
/// assembly. Every entry point may throw <see cref="DllNotFoundException"/> when the library is absent; check
/// <see cref="IsAvailable"/> first.
/// </summary>
internal static partial class LibVgmNative
{
    private const string Lib = "ppvgm";

    /// <summary><see cref="Create"/> flag: the Nuked cores (OPN2, OPM, OPLL, OPL3) for the Yamaha FM chips
    /// they cover, instead of libvgm's defaults (Genesis Plus GX / MAME, EMU2413, AdLibEmu).</summary>
    public const uint NukedFm = 0x01;

    /// <summary>Roughly the full scale of <see cref="Render"/>'s output (about 24-bit).</summary>
    public const float FullScale = 1 << 23;

    private static readonly Lazy<bool> Available = new(Probe);

    /// <summary>Whether ppvgm.dll could be loaded.</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>A player for the uncompressed VGM <paramref name="data"/> (copied), started and rendering at
    /// <paramref name="sampleRate"/>. <paramref name="rom"/>: the YRW801 sample ROM for OPL4 songs, or null.
    /// Null when the data can't be played.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_vgm_create")]
    public static unsafe partial IntPtr Create(byte* data, nuint size, uint sampleRate, uint flags, byte* rom, nuint romSize);

    /// <summary>The song's own volume adjustment, 16.16 fixed point.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_vgm_volume_gain")]
    public static partial int VolumeGain(IntPtr vgm);

    /// <summary>Renders interleaved stereo frames (a null buffer discards them). Returns the frames produced:
    /// fewer only when a song without a loop has ended.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_vgm_render")]
    public static unsafe partial uint Render(IntPtr vgm, int* buffer, uint frames);

    /// <summary>Jumps to a frame: restarts the chips afresh, then replays the song's commands up to it without
    /// rendering (see ppvgm.cpp). The result depends on the frame alone.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_vgm_seek")]
    public static partial void Seek(IntPtr vgm, uint frame);

    /// <summary>Starts the song over, with the chips created afresh.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_vgm_restart")]
    public static partial void Restart(IntPtr vgm);

    [LibraryImport(Lib, EntryPoint = "pp_vgm_destroy")]
    public static partial void Destroy(IntPtr vgm);

    private static bool Probe()
    {
        try
        {
            if (!NativeLibrary.TryLoad(Lib, typeof(LibVgmNative).Assembly, null, out var handle)) return false;
            return NativeLibrary.TryGetExport(handle, "pp_vgm_create", out _);
        }
        catch
        {
            return false;
        }
    }
}
