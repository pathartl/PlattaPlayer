using System.Runtime.InteropServices;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Counts the rand() draws each Alchemy constructor makes, and the whole <c>CToleranceVis</c>
/// construction's total, under a scripted rand(). The effects are all built inside the vis constructor,
/// BEFORE its <c>srand</c>, so for an end-to-end comparison our start-up has to consume exactly these
/// draws in exactly this order. Several counts vary with the values drawn (the OScope constructor runs
/// its randomizer), hence the several scripts.
///
/// The probed objects are leaked on purpose: their destructors free into mpvis's allocator.
///
/// Usage: probe-ctors [--sets N]
/// </summary>
internal static unsafe class ProbeCtors
{
    private static readonly (string Name, long Ctor, int Size)[] Ctors =
    [
        ("CToleranceRenderData", 0x18000a9fc, 0x400),
        ("CToleranceShift", 0x18000c62c, 0x5e8),
        ("CToleranceShiftOScope", 0x18000df64, 0x260),
        ("CTShiftLinear", 0x18000de44, 0x90),
        ("CTShiftStretch", 0x18000dedc, 0xa8),
        ("CTShiftSnafu", 0x18000dea4, 0x70),
        ("CToleranceBlur", 0x18000c004, 0x558),
        ("CToleranceBlurSwitch", 0x18000c058, 0x58),
        ("CTRenderWonderWave", 0x18000f960, 0x178),
        ("CTRenderSuperStar", 0x18000f8ec, 0x1a8),
        ("CTRenderBassBounce", 0x1800111bc, 0x70),
        ("CTStrongRenderAtomBalls", 0x18000fa0c, 0x218),
    ];

    public static int Run(string[] args)
    {
        var sets = args.Length > 1 && args[0] == "--sets" && int.TryParse(args[1], out var s) ? s : 4;

        Console.WriteLine(MpvisModule.Describe());
        Console.WriteLine(RandRedirect.SelfTest());
        Console.WriteLine();

        Console.Write($"  {"constructor",-26}");
        for (var set = 0; set < sets; set++) Console.Write($"  set{set}");
        Console.WriteLine();

        foreach (var (name, ctor, size) in Ctors)
        {
            Console.Write($"  {name,-26}");
            for (var set = 0; set < sets; set++)
            {
                RandRedirect.SetScript(RandRedirect.MakeScript(7000 + set, 4096));
                var obj = NativeMemory.AllocZeroed((nuint)size);
                ((delegate* unmanaged[Cdecl]<void*, void*>)MpvisModule.At(ctor))(obj);
                Console.Write($"  {RandRedirect.Position,4}");
            }
            Console.WriteLine();
        }

        Console.Write($"  {"CToleranceVis (CoCreate)",-26}");
        for (var set = 0; set < sets; set++)
        {
            RandRedirect.SetScript(RandRedirect.MakeScript(7000 + set, 1 << 16));
            var fx = AlchemyFactory.Create();
            Console.Write($"  {RandRedirect.Position,4}");
            GC.KeepAlive(fx);
        }
        Console.WriteLine();

        RandRedirect.Restore();
        return 0;
    }
}
