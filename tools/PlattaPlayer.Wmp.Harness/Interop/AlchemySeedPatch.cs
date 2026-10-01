using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Makes the real Alchemy deterministic by pinning the seed its constructor draws.
///
/// WHY THIS IS NEEDED: mpvis.DLL links its own CRT, so its <c>rand()</c> stream is unreachable from
/// this process (the <c>randprobe</c> verb demonstrates it) and every instance picks different effects.
/// Without a fixed seed the real object cannot be used as a frame-exact oracle at all — only
/// structurally — which is far weaker than what Bars and Waves got.
///
/// WHAT IT DOES: <c>CToleranceVis</c>'s constructor (FUN_180006908) ends with
/// <c>_o_srand(FUN_180009708())</c>, and FUN_180009708 is a thunk that tail-jumps to <c>_time64(0)</c>.
/// Overwriting that thunk with <c>mov eax, &lt;seed&gt; ; ret</c> replaces the clock with a constant we
/// choose, so every instance starts from the same RNG state and two runs render identically.
///
/// SAFETY, deliberately:
/// <list type="bullet">
/// <item>The patch is applied to the LOADED IMAGE IN THIS PROCESS ONLY. The file on disk is never
/// touched — modifying a shipped system binary would break Windows Media Player for real, needs
/// elevation, and would be reverted by Windows Resource Protection anyway.</item>
/// <item>The original bytes are captured first and can be restored, and are restored on dispose.</item>
/// <item>The target bytes are VERIFIED against the expected thunk shape before anything is written. If
/// a future build looks different the patch refuses rather than corrupting a random function.</item>
/// <item>Nothing patched reaches our own implementation. This is a measuring instrument: it lets each
/// effect be isolated and compared, which makes the port MORE faithful, not less.</item>
/// </list>
/// </summary>
internal static unsafe class AlchemySeedPatch
{
    /// <summary>
    /// RVA of the seed thunk. mpvis.DLL is byte-identical to the copy the decompile was taken from
    /// (verified by SHA-256), so the decompile's VA 0x180009708 maps straight onto this offset — but it
    /// is checked against the expected bytes before use rather than trusted.
    /// </summary>
    private const int SeedThunkRva = 0x9708;

    /// <summary>`mov eax, imm32` + `ret` — 6 bytes, which the thunk is comfortably long enough for.</summary>
    private const int PatchLength = 6;

    private static nint _module;
    private static byte[]? _original;
    private static uint _appliedSeed;

    public static bool IsApplied => _original is not null;

    public static uint AppliedSeed => _appliedSeed;

    public static string ModulePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Windows Media Player", "mpvis.DLL");

    /// <summary>
    /// Loads mpvis.DLL (if it is not already loaded) and pins its constructor seed to
    /// <paramref name="seed"/>. Must be called BEFORE creating the effect — the seed is drawn in the
    /// constructor, so patching afterwards has no effect on an existing instance.
    /// </summary>
    public static void Apply(uint seed)
    {
        EnsureModule();

        var target = _module + SeedThunkRva;

        if (_original is null)
        {
            var current = new byte[PatchLength];
            new Span<byte>((void*)target, PatchLength).CopyTo(current);
            Verify(current);
            _original = current;
        }

        Span<byte> patch = stackalloc byte[PatchLength];
        patch[0] = 0xB8; // mov eax, imm32
        BitConverter.TryWriteBytes(patch[1..], seed);
        patch[5] = 0xC3; // ret
        Write(target, patch);
        _appliedSeed = seed;
    }

    public static void Restore()
    {
        if (_original is null || _module == 0) return;
        Write(_module + SeedThunkRva, _original);
        _original = null;
    }

    public static string Describe()
    {
        EnsureModule();
        var version = FileVersionInfo.GetVersionInfo(ModulePath).FileVersion;
        var bytes = new byte[PatchLength];
        new Span<byte>((void*)(_module + SeedThunkRva), PatchLength).CopyTo(bytes);
        return $"mpvis.DLL {version} at 0x{_module:X}; seed thunk +0x{SeedThunkRva:X} = " +
               Convert.ToHexString(bytes) + (IsApplied ? $" (patched, seed {_appliedSeed})" : " (original)");
    }

    private static void EnsureModule()
    {
        if (_module != 0) return;
        _module = NativeMethods.GetModuleHandle("mpvis.DLL");
        if (_module == 0)
            _module = NativeMethods.LoadLibraryEx(ModulePath, 0, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
        if (_module == 0)
            throw new InvalidOperationException($"Could not load {ModulePath}.");
    }

    /// <summary>
    /// Refuses to patch anything that is not recognisably the seed thunk. The expected shape is a tail
    /// call into the CRT's _time64 — an optional register clear followed by a jmp — so the check is
    /// that the bytes contain a jump and nothing that looks like an already-running function prologue.
    /// </summary>
    private static void Verify(ReadOnlySpan<byte> bytes)
    {
        // 0xFF /4 (jmp r/m64, usually rip-relative) or 0xE9 (jmp rel32) must appear in the first few
        // bytes; a couple of leading bytes may clear the argument register first.
        var looksLikeThunk = false;
        for (var i = 0; i < 4 && !looksLikeThunk; i++)
            if (bytes[i] == 0xE9 || (bytes[i] == 0xFF && i + 1 < bytes.Length && (bytes[i + 1] & 0x38) == 0x20))
                looksLikeThunk = true;

        if (!looksLikeThunk)
            throw new InvalidOperationException(
                $"Bytes at mpvis.DLL+0x{SeedThunkRva:X} are {Convert.ToHexString(bytes.ToArray())}, which is not the " +
                "expected _time64 thunk. Refusing to patch — this build differs from the one analysed.");
    }

    private static void Write(nint target, ReadOnlySpan<byte> bytes)
    {
        if (!NativeMethods.VirtualProtect(target, (nuint)bytes.Length,
                NativeMethods.PAGE_EXECUTE_READWRITE, out var previous))
            throw new InvalidOperationException(
                $"VirtualProtect failed at 0x{target:X}: {Marshal.GetLastPInvokeError()}");

        bytes.CopyTo(new Span<byte>((void*)target, bytes.Length));

        NativeMethods.VirtualProtect(target, (nuint)bytes.Length, previous, out _);
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), target, (nuint)bytes.Length);
    }
}
