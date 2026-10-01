using System.Diagnostics;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Resolves addresses inside the loaded mpvis.DLL from the decompile's virtual addresses, behind an
/// identity gate.
///
/// Everything that calls mpvis's INTERNAL functions — as opposed to its COM interface — is hard-bound to
/// one exact build. A function address is just an offset; point it at a different build and it will
/// happily call into the middle of something else. The PDB is a case in point: it is an older, smaller
/// build whose addresses do NOT line up with the shipped DLL at all (PDB 0xe0a0 is SuperStar::NormalRender,
/// but 0xe0a0 in this DLL is a destructor). So this class refuses to hand out an address until the loaded
/// image matches the build the decompile was taken from.
///
/// The check is deliberately cheap and boring — file size and version — because it runs once and its job
/// is to fail loudly, not to be clever. Anything calling <see cref="At"/> should already have been
/// verified structurally too (a vtable slot pointing at the expected function, say), which is what
/// <see cref="ShiftKernelOracle"/> does.
/// </summary>
internal static unsafe class MpvisModule
{
    /// <summary>Preferred base of the decompiled image; every VA in the dump is relative to this.</summary>
    public const long ImageBase = 0x180000000;

    /// <summary>Size of the build the decompile was taken from (v12.0.26100.8115).</summary>
    private const long ExpectedSize = 221184;

    private const string ExpectedVersionPrefix = "12.0.26100";

    private static nint _handle;
    private static bool _verified;

    public static string Path => AlchemySeedPatch.ModulePath;

    /// <summary>Base address of the loaded image, loading it if necessary.</summary>
    public static nint Handle
    {
        get
        {
            if (_handle != 0) return _handle;
            _handle = NativeMethods.GetModuleHandle("mpvis.DLL");
            if (_handle == 0)
                _handle = NativeMethods.LoadLibraryEx(Path, 0, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
            if (_handle == 0)
                throw new InvalidOperationException($"Could not load {Path}.");
            return _handle;
        }
    }

    /// <summary>
    /// Translates a decompile virtual address into an address in this process. Verifies the build on
    /// first use and throws if it is not the one analysed.
    /// </summary>
    public static nint At(long virtualAddress)
    {
        AssertKnownBuild();
        if (virtualAddress < ImageBase)
            throw new ArgumentOutOfRangeException(nameof(virtualAddress),
                $"0x{virtualAddress:X} is not a decompile VA (expected >= 0x{ImageBase:X}).");
        return Handle + (nint)(virtualAddress - ImageBase);
    }

    /// <summary>Reads the qword at a decompile VA — used to read vtable slots for verification.</summary>
    public static long ReadPointer(long virtualAddress) => *(long*)At(virtualAddress);

    public static void AssertKnownBuild()
    {
        if (_verified) return;

        var info = new FileInfo(Path);
        if (!info.Exists)
            throw new InvalidOperationException($"{Path} not found.");

        var version = FileVersionInfo.GetVersionInfo(Path).FileVersion ?? "(none)";
        if (info.Length != ExpectedSize || !version.StartsWith(ExpectedVersionPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"mpvis.DLL is {info.Length} bytes, version {version}; the decompile these addresses come " +
                $"from is {ExpectedSize} bytes, version {ExpectedVersionPrefix}.x. Calling internal " +
                "functions by address across builds would land in the wrong code. Refusing.");

        _verified = true;
    }

    public static string Describe()
    {
        AssertKnownBuild();
        var version = FileVersionInfo.GetVersionInfo(Path).FileVersion;
        return $"mpvis.DLL {version} ({new FileInfo(Path).Length} bytes) loaded at 0x{Handle:X}";
    }
}
