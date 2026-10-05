using System.Runtime.InteropServices;

namespace PlattaPlayer.Codecs.Gsf.Emulation;

/// <summary>
/// P/Invoke surface of <c>mgbagsf.dll</c>: mGBA's Game Boy Advance core behind the small <c>pp_gsf_</c>
/// interface of <c>native/mgbagsf/mgbagsf.c</c>, built by <c>native/mgbagsf/build.ps1</c> and staged beside
/// the plugin assembly. Every entry point may throw <see cref="DllNotFoundException"/> when the library is
/// absent; check <see cref="IsAvailable"/> first.
/// </summary>
internal static partial class MgbaGsfNative
{
    private const string Lib = "mgbagsf";

    private static readonly Lazy<bool> Available = new(Probe);

    /// <summary>Whether mgbagsf.dll could be loaded.</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>A machine running <paramref name="image"/> (a ROM image when <paramref name="entry"/> is in
    /// cartridge space, else a multiboot image in EWRAM) from <paramref name="entry"/>, booted with the BIOS
    /// skipped. The image is copied. Null when it can't be loaded.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_gsf_create")]
    public static unsafe partial IntPtr Create(byte* image, nuint size, uint entry);

    /// <summary>The output rate, fixed: the GBA's default 32768 Hz.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_gsf_sample_rate")]
    public static partial int SampleRate();

    /// <summary>Renders <paramref name="frames"/> interleaved stereo frames (a null buffer discards them).</summary>
    [LibraryImport(Lib, EntryPoint = "pp_gsf_render")]
    public static unsafe partial void Render(IntPtr gsf, short* buffer, nuint frames);

    /// <summary>Boots the machine again, as created.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_gsf_restart")]
    public static partial void Restart(IntPtr gsf);

    [LibraryImport(Lib, EntryPoint = "pp_gsf_state_size")]
    public static partial nuint StateSize(IntPtr gsf);

    /// <summary>Saves everything the coming output depends on (<see cref="StateSize"/> bytes). 0 on success.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_gsf_save_state")]
    public static unsafe partial int SaveState(IntPtr gsf, byte* state);

    /// <summary>Restores a state saved from the same machine. 0 on success.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_gsf_load_state")]
    public static unsafe partial int LoadState(IntPtr gsf, byte* state);

    [LibraryImport(Lib, EntryPoint = "pp_gsf_destroy")]
    public static partial void Destroy(IntPtr gsf);

    private static bool Probe()
    {
        try
        {
            if (!NativeLibrary.TryLoad(Lib, typeof(MgbaGsfNative).Assembly, null, out _)) return false;
            _ = SampleRate();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
