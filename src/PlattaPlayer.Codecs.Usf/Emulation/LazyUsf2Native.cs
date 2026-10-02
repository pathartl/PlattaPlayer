using System.Runtime.InteropServices;

namespace PlattaPlayer.Codecs.Usf.Emulation;

/// <summary>
/// P/Invoke surface of <c>lazyusf2.dll</c>: kode54's lazyusf2 (<c>usf/usf.h</c>), built by
/// <c>native/lazyusf2/build.ps1</c> and staged beside the plugin assembly. Rendering goes through the
/// <c>pp_</c> wrappers, which restore the caller's floating-point rounding mode (the emulated FPU changes it).
/// Every entry point may throw <see cref="DllNotFoundException"/> when the library is absent; check
/// <see cref="IsAvailable"/> first.
/// </summary>
internal static partial class LazyUsf2Native
{
    private const string Lib = "lazyusf2";

    private static readonly Lazy<bool> Available = new(Probe);

    /// <summary>Whether lazyusf2.dll could be loaded.</summary>
    public static bool IsAvailable => Available.Value;

    [LibraryImport(Lib, EntryPoint = "usf_get_state_size")]
    public static partial nuint GetStateSize();

    [LibraryImport(Lib, EntryPoint = "usf_clear")]
    public static partial void Clear(IntPtr state);

    [LibraryImport(Lib, EntryPoint = "usf_set_compare")]
    public static partial void SetCompare(IntPtr state, int enable);

    [LibraryImport(Lib, EntryPoint = "usf_set_fifo_full")]
    public static partial void SetFifoFull(IntPtr state, int enable);

    [LibraryImport(Lib, EntryPoint = "usf_set_hle_audio")]
    public static partial void SetHleAudio(IntPtr state, int enable);

    /// <summary>Returns -1 on invalid data, 0 on success.</summary>
    [LibraryImport(Lib, EntryPoint = "usf_upload_section")]
    public static unsafe partial int UploadSection(IntPtr state, byte* data, nuint size);

    /// <summary>Renders <paramref name="count"/> stereo frames at the emulated rate (null buffer: discards
    /// them; zero count: starts emulation and reports the rate). Returns null or an error message.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_usf_render")]
    public static unsafe partial IntPtr Render(IntPtr state, short* buffer, nuint count, int* sampleRate);

    /// <summary>As <see cref="Render"/>, resampled to <paramref name="sampleRate"/>.</summary>
    [LibraryImport(Lib, EntryPoint = "pp_usf_render_resampled")]
    public static unsafe partial IntPtr RenderResampled(IntPtr state, short* buffer, nuint count, int sampleRate);

    [LibraryImport(Lib, EntryPoint = "pp_usf_restart")]
    public static partial void Restart(IntPtr state);

    [LibraryImport(Lib, EntryPoint = "usf_shutdown")]
    public static partial void Shutdown(IntPtr state);

    private static bool Probe()
    {
        try
        {
            if (!NativeLibrary.TryLoad(Lib, typeof(LazyUsf2Native).Assembly, null, out _)) return false;
            _ = GetStateSize();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
