using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Resolves which property setter each vtable slot used by <c>SetCurrentPreset</c> actually calls.
///
/// The decompile shows the preset table as a series of indirect calls — <c>vtbl+0x40</c>,
/// <c>vtbl+0xf0</c>, <c>vtbl+0x120</c> and so on — with the target functions unresolved, so the mapping
/// from slot to field cannot be read off the source. Guessing it produced a wrong bar geometry (a 1 px
/// gap where the real effect has none), which is exactly the class of mistake this whole exercise
/// exists to eliminate. Reading the vtable out of a LIVE object settles it.
///
/// Setter -> field mapping comes from the decompile, where each setter's single store is unambiguous.
/// </summary>
internal static class DumpBarsVtable
{
    private static readonly (long Rva, string Field)[] KnownSetters =
    [
        (0x41F060, "Gap            (+0x5f8)"),
        (0x41F9B0, "BarWidth       (+0x5c0)"),
        (0x41EAC0, "Style          (+0x5d8)"),
        (0x420120, "PeakHoldFrames (+0x5cc)"),
        (0x41F600, "FallSpeed      (+0x5b4)"),
        (0x41EE80, "FadeStep       (+0x5d4)"),
        (0x41ECA0, "TrailMode      (+0x5dc)"),
    ];

    /// <summary>The slots SetCurrentPreset invokes, in the order they appear in FUN_18041e270.</summary>
    private static readonly int[] PresetSlots = [0x40, 0x60, 0xA0, 0xE0, 0xF0, 0x110, 0x120];

    /// <summary>
    /// The live build's .text sits a small fixed distance from the decompiled one, so match each slot
    /// to the nearest known setter and report the offset rather than requiring an exact hit.
    /// </summary>
    private const long MaxDrift = 0x40;

    public static unsafe int Run(string[] args)
    {
        WmpInternalFactory.Load();
        var factory = WmpInternalFactory.Resolve(WmpGuids.ClsidBarsAndWaves);
        using var fx = WmpInternalFactory.CreateInstance(factory);

        // WmpEffect holds the IWMPEffects sub-interface, which sits at object+0x50; the property vtable
        // is the one at the object base.
        var iface = fx.RawPointer;
        var objectBase = iface - 0x50;
        var vtable = *(nint*)objectBase;
        var module = WmpInternalFactory.Module;

        Console.WriteLine($"   object base   0x{objectBase:X}");
        Console.WriteLine($"   property vtbl 0x{vtable:X}  (RVA 0x{vtable - module:X})");
        Console.WriteLine();
        Console.WriteLine("   slots invoked by SetCurrentPreset:");

        foreach (var slot in PresetSlots)
        {
            var fn = *(nint*)(vtable + slot);
            var rva = (long)(fn - module);
            var best = KnownSetters
                .Select(k => (k.Field, Delta: rva - k.Rva))
                .OrderBy(k => Math.Abs(k.Delta))
                .First();
            var label = Math.Abs(best.Delta) <= MaxDrift
                ? $"{best.Field}   (decompile +0x{best.Delta:X})"
                : "<not one of the known setters>";
            Console.WriteLine($"     +0x{slot:X3}  ->  RVA 0x{rva:X}  {label}");
        }
        return 0;
    }
}
