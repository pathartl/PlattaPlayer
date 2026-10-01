using System.Text;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Extracts the three lookup tables wmp.dll's bar renderer uses to turn TimedLevel frequency bytes into
/// bar heights, straight out of the loaded image. Modelling them from a fitted closed form would bake in
/// an approximation error on every bar of every frame; reading the real bytes costs nothing and is exact.
///
/// From FUN_18041cf78:
///   expand[b]      256 floats  — frequency byte -> a linear energy, spanning eight decades
///   compress via   int  = mantissa[(bits(f) >> 12) &amp; 0x7ff] + exponent[(bits(f) >> 23) &amp; 0xff]
///                  clamped to 0..255. A table-driven log2: the exponent byte supplies the integer part
///                  and the mantissa bits the fraction.
///
/// The tables are located by CONTENT (a 256-entry geometric ladder ending near 1e8), never by absolute
/// address, and are then proved correct by round-tripping every byte value 0..255 through both
/// directions. That round-trip is the real validation — if the wrong bytes were picked up it fails.
/// </summary>
internal static class DumpLut
{
    private const int ExponentTableCount = 256;
    private const int MantissaTableCount = 2048;

    public static int Run(string[] args)
    {
        WmpInternalFactory.Load();

        var expandAddr = FindExpansionTable();
        if (expandAddr == 0)
        {
            Console.Error.WriteLine("   could not locate the 256-entry expansion table in wmp.dll .rdata.");
            return 1;
        }

        var expand = ReadFloats(expandAddr, 256);

        // The decompile's relative offsets do not hold in the live build, so find the two integer
        // tables by their shape instead. Together they compute 255/8 * log10(E): the exponent table
        // supplies the integer part and therefore steps by 255/(8*log2 10) ~= 9.59 per binary exponent,
        // while the mantissa table supplies the fraction and so is non-decreasing over 0..9.
        var exponentAddr = FindExponentTable();
        var mantissaAddr = exponentAddr == 0 ? 0 : FindMantissaTable();
        if (exponentAddr == 0 || mantissaAddr == 0)
        {
            Console.WriteLine("   The integer log tables could not be located by shape in this build.");
            Console.WriteLine("   Falling back to the closed form they implement (see BarsLevelTables.Compress).");
            EmitClosedForm(expand);
            return 0;
        }

        var exponent = ReadInts(exponentAddr, ExponentTableCount);
        var mantissa = ReadInts(mantissaAddr, MantissaTableCount);

        Console.WriteLine($"   expansion table at 0x{expandAddr:X}");
        Console.WriteLine($"     [0]={expand[0]:G6}  [1]={expand[1]:G6}  [2]={expand[2]:G6}  " +
                          $"[128]={expand[128]:G6}  [255]={expand[255]:G6}");
        Console.WriteLine($"     ratio [2]/[1] = {expand[2] / expand[1]:G8}   (10^(8/255) = {Math.Pow(10, 8 / 255.0):G8})");
        Console.WriteLine($"   exponent table at 0x{exponentAddr:X} ({ExponentTableCount} ints)");
        Console.WriteLine($"     [0..7]     {string.Join(", ", exponent.Take(8))}");
        Console.WriteLine($"     [120..135] {string.Join(", ", exponent.Skip(120).Take(16))}");
        Console.WriteLine($"     min {exponent.Min()}  max {exponent.Max()}");
        Console.WriteLine($"   mantissa table at 0x{mantissaAddr:X} ({MantissaTableCount} ints)");
        Console.WriteLine($"     [0..11]    {string.Join(", ", mantissa.Take(12))}");
        Console.WriteLine($"     [1024..1035] {string.Join(", ", mantissa.Skip(1024).Take(12))}");
        Console.WriteLine($"     min {mantissa.Min()}  max {mantissa.Max()}");
        Console.WriteLine();

        // Round-trip: a single bin at level v, fully covering one band, expands to expand[v] and must
        // compress back to v. This proves all three tables at once.
        var failures = 0;
        var worst = 0;
        for (var v = 0; v < 256; v++)
        {
            var back = Compress(expand[v], mantissa, exponent);
            var delta = Math.Abs(back - v);
            if (v == 0) continue; // expand[0] is a hard zero (silence), which has no logarithm
            if (delta > worst) worst = delta;
            if (delta > 1) failures++;
        }
        Console.WriteLine($"   round-trip over byte values 1..255: worst error {worst}, {failures} value(s) off by >1");

        var ok = failures == 0 && worst <= 1;
        Console.WriteLine(ok
            ? "   [ok] the tables invert each other — extraction is correct."
            : "   [FAIL] round-trip broken; the located tables are not the right ones.");
        if (!ok) return 1;

        Directory.CreateDirectory(HarnessPaths.DumpDir);
        var path = Path.Combine(HarnessPaths.DumpDir, "BarsLevelTables.g.cs");
        File.WriteAllText(path, Emit(expand, mantissa, exponent));
        Console.WriteLine($"   wrote {path}");
        return 0;
    }

    /// <summary>The exact conversion from FUN_18041cf78, reproduced here to validate the tables.</summary>
    private static int Compress(float energy, int[] mantissa, int[] exponent)
    {
        var bits = BitConverter.SingleToUInt32Bits(energy);
        var v = mantissa[(int)((bits >> 12) & 0x7FF)] + exponent[(int)((bits >> 23) & 0xFF)];
        return v >= 0x100 ? 255 : v < 0 ? 0 : v;
    }

    /// <summary>255/8 * log10(2) — the byte-per-binary-exponent step the exponent table must show.</summary>
    private static readonly double ExponentStep = 255.0 / 8.0 * Math.Log10(2);

    private static unsafe nint FindExponentTable()
    {
        var rdata = WmpInternalFactory.GetSection(".rdata");
        var p = (byte*)rdata.Base;
        var limit = rdata.Size - ExponentTableCount * 4;
        for (var off = 0; off <= limit; off += 4)
        {
            var v = (int*)(p + off);
            // Anchor on the unbiased-zero point: a float exponent byte of 127 means 2^0, so the table
            // must contribute 0 there, and neighbouring entries must step by ~9.59.
            if (v[127] != 0) continue;
            var ok = true;
            for (var i = 100; i < 160 && ok; i++)
                if (Math.Abs(v[i] - ExponentStep * (i - 127)) > 1.5) ok = false;
            if (ok) return rdata.Base + off;
        }
        return 0;
    }

    private static unsafe nint FindMantissaTable()
    {
        var rdata = WmpInternalFactory.GetSection(".rdata");
        var p = (byte*)rdata.Base;
        var limit = rdata.Size - MantissaTableCount * 4;
        for (var off = 0; off <= limit; off += 4)
        {
            var v = (int*)(p + off);
            if (v[0] != 0) continue;
            var ok = true;
            var last = 0;
            for (var i = 0; i < MantissaTableCount && ok; i++)
            {
                if (v[i] < 0 || v[i] > 10 || v[i] < last) ok = false;
                last = v[i];
            }
            // The fraction must actually sweep the full 0..9 range, not sit flat.
            if (ok && last >= 9) return rdata.Base + off;
        }
        return 0;
    }

    /// <summary>
    /// Emits just the expansion table when the binary's integer log tables cannot be located by shape.
    /// Those two tables exist only as a fast integer log2; the closed form they approximate is
    /// 255/8 * log10(E), and the resulting bar heights get verified frame-by-frame against captured
    /// ground truth rather than taken on trust.
    /// </summary>
    private static void EmitClosedForm(float[] expand)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Expansion table extracted verbatim from wmp.dll by the harness ('dump-lut').");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        sb.AppendLine("namespace PlattaPlayer.Visualizations.Wmp.BarsAndWaves;");
        sb.AppendLine();
        sb.AppendLine("internal static partial class BarsLevelTables");
        sb.AppendLine("{");
        AppendFloatArray(sb, "Expand", expand,
            "Frequency byte -> linear energy. Spans eight decades; index 0 is a hard zero for silence.");
        sb.AppendLine("}");

        Directory.CreateDirectory(HarnessPaths.DumpDir);
        var path = Path.Combine(HarnessPaths.DumpDir, "BarsLevelTables.g.cs");
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"   wrote {path} (expansion table only)");
    }

    private static nint FindExpansionTable()
    {
        var rdata = WmpInternalFactory.GetSection(".rdata");
        var expectedRatio = (float)Math.Pow(10, 8 / 255.0);

        unsafe
        {
            var p = (byte*)rdata.Base;
            var limit = rdata.Size - 256 * 4;
            for (var off = 0; off <= limit; off += 4)
            {
                var f = (float*)(p + off);
                if (f[0] != 0f) continue;
                if (Math.Abs(f[1] - expectedRatio) > 1e-3f) continue;
                if (f[255] < 9e7f || f[255] > 1.1e8f) continue;

                var geometric = true;
                for (var i = 2; i < 256 && geometric; i++)
                {
                    var r = f[i] / f[i - 1];
                    if (Math.Abs(r - expectedRatio) > 1e-3f) geometric = false;
                }
                if (geometric) return rdata.Base + off;
            }
        }
        return 0;
    }

    private static unsafe float[] ReadFloats(nint addr, int count)
    {
        var a = new float[count];
        var p = (float*)addr;
        for (var i = 0; i < count; i++) a[i] = p[i];
        return a;
    }

    private static unsafe int[] ReadInts(nint addr, int count)
    {
        var a = new int[count];
        var p = (int*)addr;
        for (var i = 0; i < count; i++) a[i] = p[i];
        return a;
    }

    private static string Emit(float[] expand, int[] mantissa, int[] exponent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Extracted verbatim from wmp.dll by the PlattaPlayer WMP harness ('dump-lut').");
        sb.AppendLine("// These are the real tables wmp.dll's \"Bars and Waves\" renderer uses; do not hand-edit,");
        sb.AppendLine("// and do not replace them with a fitted closed form.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();
        sb.AppendLine("namespace PlattaPlayer.Visualizations.Wmp.BarsAndWaves;");
        sb.AppendLine();
        sb.AppendLine("internal static class BarsLevelTables");
        sb.AppendLine("{");
        AppendFloatArray(sb, "Expand", expand,
            "Frequency byte -> linear energy. Spans eight decades; index 0 is a hard zero for silence.");
        sb.AppendLine();
        AppendIntArray(sb, "Mantissa", mantissa,
            "Indexed by (bits(energy) >> 12) & 0x7FF — the fractional part of the table-driven log2.");
        sb.AppendLine();
        AppendIntArray(sb, "Exponent", exponent,
            "Indexed by (bits(energy) >> 23) & 0xFF — the integer part of the table-driven log2.");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendFloatArray(StringBuilder sb, string name, float[] values, string doc)
    {
        sb.AppendLine($"    /// <summary>{doc}</summary>");
        sb.AppendLine($"    public static readonly float[] {name} =");
        sb.AppendLine("    [");
        for (var i = 0; i < values.Length; i += 6)
        {
            var row = values.Skip(i).Take(6).Select(v => $"{v:R}f");
            sb.AppendLine($"        {string.Join(", ", row)},");
        }
        sb.AppendLine("    ];");
    }

    private static void AppendIntArray(StringBuilder sb, string name, int[] values, string doc)
    {
        sb.AppendLine($"    /// <summary>{doc}</summary>");
        sb.AppendLine($"    public static readonly int[] {name} =");
        sb.AppendLine("    [");
        for (var i = 0; i < values.Length; i += 12)
        {
            var row = values.Skip(i).Take(12).Select(v => v.ToString());
            sb.AppendLine($"        {string.Join(", ", row)},");
        }
        sb.AppendLine("    ];");
    }
}
