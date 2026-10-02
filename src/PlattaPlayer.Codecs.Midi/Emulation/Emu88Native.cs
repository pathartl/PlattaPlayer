using System.Runtime.InteropServices;

namespace PlattaPlayer.Codecs.Midi.Emulation;

/// <summary>
/// P/Invoke surface of <c>emu88.dll</c>: gearmulator's 88emu (Roland Sound Canvas / MT-32 / CM low-level
/// emulation) exporting its C interface, <c>88lib/c_interface.h</c>. Built by <c>native/emu88/build.ps1</c>
/// and staged beside the plugin assembly. Every entry point may throw <see cref="DllNotFoundException"/> when
/// the library is absent; <see cref="Emu88Library"/> probes for it once before anything else calls in.
/// </summary>
internal static partial class Emu88Native
{
    private const string Lib = "emu88";

    public const int RcOk = 0;

    public const uint BootFactoryReset = 1;
    public const uint BootSkipIntro = 2;
    public const uint BootDefault = BootFactoryReset | BootSkipIntro;

    [LibraryImport(Lib, EntryPoint = "emu88_get_library_version_string")]
    public static partial IntPtr GetLibraryVersionString();

    [LibraryImport(Lib, EntryPoint = "emu88_set_rom_path", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetRomPath(string path);

    [LibraryImport(Lib, EntryPoint = "emu88_rescan_roms")]
    public static partial void RescanRoms();

    [LibraryImport(Lib, EntryPoint = "emu88_get_device_count")]
    public static partial int GetDeviceCount();

    [LibraryImport(Lib, EntryPoint = "emu88_get_device_id")]
    public static partial int GetDeviceId(int index);

    [LibraryImport(Lib, EntryPoint = "emu88_get_device_name")]
    public static partial IntPtr GetDeviceName(int device);

    [LibraryImport(Lib, EntryPoint = "emu88_is_device_available")]
    public static partial int IsDeviceAvailable(int device);

    [LibraryImport(Lib, EntryPoint = "emu88_create_context")]
    public static partial IntPtr CreateContext();

    [LibraryImport(Lib, EntryPoint = "emu88_free_context")]
    public static partial void FreeContext(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "emu88_select_device")]
    public static partial int SelectDevice(IntPtr context, int device);

    [LibraryImport(Lib, EntryPoint = "emu88_set_boot_flags")]
    public static partial void SetBootFlags(IntPtr context, uint flags);

    [LibraryImport(Lib, EntryPoint = "emu88_set_stereo_output_samplerate")]
    public static partial void SetStereoOutputSamplerate(IntPtr context, double samplerate);

    [LibraryImport(Lib, EntryPoint = "emu88_open_synth")]
    public static partial int OpenSynth(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "emu88_get_actual_stereo_output_samplerate")]
    public static partial uint GetActualStereoOutputSamplerate(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "emu88_play_msg")]
    public static partial int PlayMsg(IntPtr context, uint msg);

    [LibraryImport(Lib, EntryPoint = "emu88_play_sysex")]
    public static partial int PlaySysex(IntPtr context, ReadOnlySpan<byte> sysex, uint length);

    [LibraryImport(Lib, EntryPoint = "emu88_play_device_reset")]
    public static partial int PlayDeviceReset(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "emu88_play_silence")]
    public static partial int PlaySilence(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "emu88_render_float")]
    public static unsafe partial void RenderFloat(IntPtr context, float* stream, uint length);

    public static string? PtrToString(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
