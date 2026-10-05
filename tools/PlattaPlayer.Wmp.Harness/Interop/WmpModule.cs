using System.Diagnostics;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Resolves addresses inside the loaded wmp.dll from the Ghidra VAs of the build the Battery work was
/// read from, behind an identity gate. It is the wmp.dll twin of <see cref="MpvisModule"/>.
///
/// wmp.dll is serviced far more often than mpvis (8875, 8972, 9278 in a few months), and every update
/// moves code. A function VA from one build points into the middle of something else in the next, so
/// nothing may call an internal function by address until the loaded image matches the analysed one.
///
/// THE ANALYSED BUILD is 12.0.26100.9278 x64 (Ghidra project <c>Wmp9278.gpr</c>). Its names come from the
/// Win7 SP1 x86 build (12.0.7601.17514), the only wmp.dll whose PDB Microsoft publishes
/// (<c>wmp_notestroot.pdb</c>). They are carried across by anchors, not by address.
/// </summary>
internal static unsafe class WmpModule
{
    /// <summary>Preferred base of the analysed image; every VA in the Ghidra project is relative to it.</summary>
    public const long ImageBase = 0x180000000;

    /// <summary>File size of 12.0.26100.9278 x64.</summary>
    private const long ExpectedSize = 10629120;

    private const string ExpectedVersionPrefix = "12.0.26100.9278";

    private static bool _verified;

    public static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wmp.dll");

    /// <summary>Base address of the loaded image, loading it if necessary.</summary>
    public static nint Handle
    {
        get
        {
            WmpInternalFactory.Load();
            return WmpInternalFactory.Module;
        }
    }

    /// <summary>Translates an analysed-build VA into this process. Verifies the build on first use.</summary>
    public static nint At(long virtualAddress)
    {
        AssertKnownBuild();
        if (virtualAddress < ImageBase)
            throw new ArgumentOutOfRangeException(nameof(virtualAddress),
                $"0x{virtualAddress:X} is not a wmp.dll VA (expected >= 0x{ImageBase:X}).");
        return Handle + (nint)(virtualAddress - ImageBase);
    }

    /// <summary>The analysed-build VA of a pointer into the loaded image (for reporting vtable slots).</summary>
    public static long VaOf(nint address) => (long)address - Handle + ImageBase;

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
                $"wmp.dll is {info.Length} bytes, version {version}; the Battery addresses come from " +
                $"{ExpectedVersionPrefix} ({ExpectedSize} bytes). A servicing update has moved the code — " +
                "re-run the Wmp9278 symbol port against the new build before calling anything by address.");

        _verified = true;
    }

    public static string Describe()
    {
        AssertKnownBuild();
        var version = FileVersionInfo.GetVersionInfo(Path).FileVersion;
        return $"wmp.dll {version} ({new FileInfo(Path).Length} bytes) loaded at 0x{Handle:X}";
    }
}
