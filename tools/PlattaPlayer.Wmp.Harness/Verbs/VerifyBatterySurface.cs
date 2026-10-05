using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.Wmp.Battery;
using PlattaPlayer.Visualizations.Wmp.Battery.Shifts;
using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Diffs Battery's pixel primitives against the real functions in wmp.dll, run on stub surfaces whose
/// bits are arrays we own:
/// <list type="bullet">
/// <item><c>CBatterySurface::DrawLine</c> / <c>DrawClippedLine</c>: random segments, in range and
/// beyond it.</item>
/// <item><c>CBatterySurface::DrawJCurve</c>: random arcs over every direction mode, joined and
/// dotted.</item>
/// <item><c>CPlusBlur::Perform</c>: a real CPlusBlur built by its constructor and given the
/// normal-frame tables, over random fields.</item>
/// <item><c>CShiftTransform::Perform</c>: a real shift's table gathered through the real transform,
/// including the front/back swap.</item>
/// </list>
/// The surfaces start as a non-black random field, so "wrote nothing" and "wrote the wrong value" can
/// never look alike.
///
/// A CBatterySurface (0x28 bytes) needs only W (+0x08), H (+0x0c) and bits (+0x20) for these functions.
/// A CRenderData needs only W/H (+0x5c/+0x60) and the front/back pointers (+0xb8/+0xc0).
///
/// Usage: verify-battery-surface [--cases N] [--seed N] [--size WxH]
/// </summary>
internal static unsafe class VerifyBatterySurface
{
    private const long DrawLineVa = 0x180414050;
    private const long DrawClippedLineVa = 0x180413c20;
    private const long DrawJCurveVa = 0x180413ca0;
    private const long PlusBlurCtorVa = 0x18040f434;
    private const long SetupFadeTableVa = 0x1804137f8;
    private const long SetupBlurTableVa = 0x1804137a0;
    private const long PlusBlurPerformVa = 0x180412250;
    private const long ShiftTransformPerformVa = 0x180412460;

    public static int Run(string[] args)
    {
        var cases = VerifyWarps.ArgInt(args, "--cases", 400);
        var seed = VerifyWarps.ArgInt(args, "--seed", 1000);
        var (w, h) = Capture.ArgSize(args, "--size", 384, 288);
        Console.WriteLine(WmpModule.Describe());

        var failures = 0;
        failures += Lines(cases, seed, w, h);
        failures += Curves(cases, seed + 1, w, h);
        failures += Blur(Math.Max(4, cases / 50), seed + 2, w, h);
        failures += Transform(seed + 3, w, h);

        Console.WriteLine(failures == 0 ? "verify-battery-surface: ALL EXACT" : $"verify-battery-surface: {failures} check(s) diverge");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>A stub CBatterySurface over a pinned array.</summary>
    private sealed class StubSurface : IDisposable
    {
        private readonly GCHandle _pin;

        public StubSurface(byte[] bits, int w, int h)
        {
            Bits = bits;
            _pin = GCHandle.Alloc(bits, GCHandleType.Pinned);
            Pointer = (nint)NativeMemory.AllocZeroed(0x28);
            *(int*)(Pointer + 0x08) = w;
            *(int*)(Pointer + 0x0c) = h;
            *(nint*)(Pointer + 0x20) = _pin.AddrOfPinnedObject();
        }

        public byte[] Bits { get; }

        public nint Pointer { get; }

        public void Dispose()
        {
            NativeMemory.Free((void*)Pointer);
            _pin.Free();
        }
    }

    private static byte[] Field(Random r, int n)
    {
        var b = new byte[n];
        r.NextBytes(b);
        return b;
    }

    private static int Compare(string what, byte[] real, byte[] ours, int w)
    {
        for (var i = 0; i < real.Length; i++)
        {
            if (real[i] == ours[i]) continue;
            Console.WriteLine($"  FAIL {what}: pixel ({i % w},{i / w}) real {real[i]} ours {ours[i]}");
            return 1;
        }
        return 0;
    }

    private static int Lines(int cases, int seed, int w, int h)
    {
        var r = new Random(seed);
        var line = (delegate* unmanaged<nint, int, int, int, int, byte, void>)WmpModule.At(DrawLineVa);
        var clipped = (delegate* unmanaged<nint, int, int, int, int, byte, void>)WmpModule.At(DrawClippedLineVa);
        var bg = Field(r, w * h);
        int fails = 0, count = 0;
        for (var c = 0; c < cases && fails == 0; c++)
        {
            var useClip = c % 2 == 1;
            int x1, y1, x2, y2;
            if (useClip)
            {
                x1 = r.Next(-200, w + 200); y1 = r.Next(-200, h + 200);
                x2 = r.Next(-200, w + 200); y2 = r.Next(-200, h + 200);
            }
            else
            {
                x1 = r.Next(w); y1 = r.Next(h);
                // Mix axis-aligned, diagonal and general segments.
                (x2, y2) = (c % 7) switch
                {
                    0 => (x1, r.Next(h)),
                    2 => (r.Next(w), y1),
                    4 => DiagonalEnd(x1, y1, r.Next(-60, 61), r.Next(2) == 0, w, h),
                    _ => (r.Next(w), r.Next(h)),
                };
            }
            var col = (byte)r.Next(256);
            var real = (byte[])bg.Clone();
            using (var s = new StubSurface(real, w, h))
                (useClip ? clipped : line)(s.Pointer, x1, y1, x2, y2, col);
            var ours = new BatterySurface(w, h);
            bg.CopyTo(ours.Bits, 0);
            if (useClip) ours.DrawClippedLine(x1, y1, x2, y2, col);
            else ours.DrawLine(x1, y1, x2, y2, col);
            fails += Compare($"{(useClip ? "DrawClippedLine" : "DrawLine")}({x1},{y1})-({x2},{y2})", real, ours.Bits, w);
            count++;
        }
        Console.WriteLine($"{(fails == 0 ? "  ok  " : "  FAIL")} lines: {count} cases");
        return fails;
    }

    private static (int, int) DiagonalEnd(int x, int y, int d, bool flip, int w, int h)
    {
        var x2 = Math.Clamp(x + d, 0, w - 1);
        var y2 = Math.Clamp(y + (flip ? -1 : 1) * Math.Abs(x2 - x), 0, h - 1);
        return (x2, y2);
    }

    private static int Curves(int cases, int seed, int w, int h)
    {
        var r = new Random(seed);
        var curve = (delegate* unmanaged<nint, int, int, int, int, int, int, int, byte, byte, byte, byte, void>)
            WmpModule.At(DrawJCurveVa);
        var bg = Field(r, w * h);
        int fails = 0, count = 0;
        for (var c = 0; c < cases && fails == 0; c++)
        {
            int x1 = r.Next(-40, w + 40), y1 = r.Next(-40, h + 40), x2 = r.Next(-40, w + 40), y2 = r.Next(-40, h + 40);
            int cx = r.Next(w), cy = r.Next(h);
            if (c % 9 == 0) x1 = cx; // the dx == 0 quirk
            var n = c % 13 == 0 ? r.Next(-2, 3) : r.Next(1, 120);
            byte c0 = (byte)r.Next(256), c1 = (byte)r.Next(256);
            var connect = r.Next(2) == 0;
            var dir = (byte)(c % 6);
            var real = (byte[])bg.Clone();
            using (var s = new StubSurface(real, w, h))
                curve(s.Pointer, x1, y1, x2, y2, cx, cy, n, c0, c1, connect ? (byte)1 : (byte)0, dir);
            var ours = new BatterySurface(w, h);
            bg.CopyTo(ours.Bits, 0);
            ours.DrawJCurve(x1, y1, x2, y2, cx, cy, n, c0, c1, connect, dir);
            fails += Compare($"DrawJCurve(({x1},{y1})->({x2},{y2}) about ({cx},{cy}) n {n} c {c0}->{c1} join {connect} dir {dir})",
                real, ours.Bits, w);
            count++;
        }
        Console.WriteLine($"{(fails == 0 ? "  ok  " : "  FAIL")} DrawJCurve: {count} cases");
        return fails;
    }

    private static nint StubRenderData(int w, int h, StubSurface front, StubSurface back)
    {
        var rd = (nint)NativeMemory.AllocZeroed(0x100);
        *(int*)(rd + 0x5c) = w;
        *(int*)(rd + 0x60) = h;
        *(nint*)(rd + 0xb8) = front.Pointer;
        *(nint*)(rd + 0xc0) = back.Pointer;
        return rd;
    }

    private static int Blur(int cases, int seed, int w, int h)
    {
        var r = new Random(seed);
        var blur = (nint)NativeMemory.AllocZeroed(0x608);
        ((delegate* unmanaged<nint, nint>)WmpModule.At(PlusBlurCtorVa))(blur);
        ((delegate* unmanaged<nint, void>)WmpModule.At(SetupFadeTableVa))(blur);
        ((delegate* unmanaged<nint, byte, void>)WmpModule.At(SetupBlurTableVa))(blur, 1);

        var table = new ReadOnlySpan<byte>((void*)(blur + 0x08), 0x500);
        for (var i = 0; i < 0x500; i++)
        {
            if (table[i] == PlusBlur.Table[i]) continue;
            Console.WriteLine($"  FAIL blur table[{i}] real {table[i]} ours {PlusBlur.Table[i]}");
            return 1;
        }

        var fails = 0;
        for (var c = 0; c < cases && fails == 0; c++)
        {
            var src = Field(r, w * h);
            if (c % 2 == 1) for (var i = 0; i < src.Length; i++) src[i] = (byte)(src[i] % 3 == 0 ? 255 : src[i] & 7); // extremes
            var a = (byte[])src.Clone();
            var b = Field(r, w * h);
            using var sa = new StubSurface(a, w, h);
            using var sb = new StubSurface(b, w, h);
            var rd = StubRenderData(w, h, sa, sb); // front = a (latest), back = b
            ((delegate* unmanaged<nint, nint, void>)WmpModule.At(PlusBlurPerformVa))(blur, rd);
            // Perform swaps first, so it reads a (now back) and writes b (now front).
            if (*(nint*)(rd + 0xb8) != sb.Pointer)
            {
                Console.WriteLine("  FAIL blur: expected the front/back swap");
                return 1;
            }
            var ours = new byte[w * h];
            PlusBlur.Perform(src, ours, w, h);
            fails += Compare("CPlusBlur::Perform", b, ours, w);
            NativeMemory.Free((void*)rd);
        }
        Console.WriteLine($"{(fails == 0 ? "  ok  " : "  FAIL")} CPlusBlur: table + {cases} fields");
        return fails;
    }

    private static int Transform(int seed, int w, int h)
    {
        // A real Shiitake table, built by the real Setup, gathered by the real CShiftTransform.
        RandRedirect.ApplyWmp();
        var script = RandRedirect.MakeScript(seed, w * h * 2 + 64);
        RandRedirect.SetScript(script);
        var shift = BatteryOracle.Shift(14);
        shift.SetSize(w, h, false);
        shift.Randomize();
        shift.MarkDirty();
        while (!shift.IsComplete) shift.Setup();

        var r = new Random(seed);
        var src = Field(r, w * h);
        var a = (byte[])src.Clone();
        var b = Field(r, w * h);
        using var sa = new StubSurface(a, w, h);
        using var sb = new StubSurface(b, w, h);
        var rd = StubRenderData(w, h, sa, sb);
        var xf = (nint)NativeMemory.AllocZeroed(0x10);
        *(nint*)(xf + 8) = shift.Pointer; // m_table (no AddRef needed: we never release)
        ((delegate* unmanaged<nint, nint, void>)WmpModule.At(ShiftTransformPerformVa))(xf, rd);
        RandRedirect.Restore();

        var table = shift.Table;
        var ours = new byte[w * h];
        for (var i = 0; i < ours.Length; i++) ours[i] = src[table[i]];
        var fails = Compare("CShiftTransform::Perform", b, ours, w);
        Console.WriteLine($"{(fails == 0 ? "  ok  " : "  FAIL")} CShiftTransform: gather + swap");
        return fails;
    }
}
