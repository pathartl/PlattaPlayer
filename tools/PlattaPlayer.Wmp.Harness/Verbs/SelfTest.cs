using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Verifies the harness's own GDI surface behaves the way the real WMP host's does, before any capture
/// is trusted. The 32-bpp assertion is the important one: wmp.dll's Bars picks its internal DIB depth
/// from <c>max(GetDeviceCaps(hdc, BITSPIXEL), 8)</c>, so a 1-bpp memory DC would silently exercise a
/// code path that never runs inside WMP.
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var failures = 0;

        using (var dib = new DibTarget(640, 480))
        {
            var bpp = dib.BitsPerPixel;
            failures += Check("memory DC reports 32 bpp once the DIB is selected", bpp == 32, $"got {bpp}");

            dib.ClearBlack();
            var black = dib.Snapshot();
            var allBlack = true;
            for (var i = 0; i < black.Length; i += 4)
                if (black[i] != 0 || black[i + 1] != 0 || black[i + 2] != 0) { allBlack = false; break; }
            failures += Check("PatBlt(BLACKNESS) clears the surface", allBlack, "surface was not black");

            failures += Check("snapshot is width*height*4 bytes",
                black.Length == 640 * 480 * 4, $"got {black.Length}");

            failures += Check("snapshot normalises alpha to 0xFF", black[3] == 0xFF, $"got 0x{black[3]:X2}");

            // Write a recognisable pattern straight into the DIB bits and read it back, proving the
            // pointer we hand to the effects is the same memory we later diff.
            var bits = dib.Pixels;
            for (var i = 0; i < bits.Length; i += 4)
            {
                var px = i / 4;
                bits[i + 0] = (byte)(px & 0xFF);
                bits[i + 1] = (byte)((px >> 8) & 0xFF);
                bits[i + 2] = (byte)((px >> 16) & 0xFF);
            }
            var readBack = dib.Snapshot();
            var roundTrip = true;
            for (var i = 0; i < readBack.Length; i += 4)
            {
                var px = i / 4;
                if (readBack[i] != (byte)(px & 0xFF) ||
                    readBack[i + 1] != (byte)((px >> 8) & 0xFF) ||
                    readBack[i + 2] != (byte)((px >> 16) & 0xFF)) { roundTrip = false; break; }
            }
            failures += Check("DIB pixel readback round-trips", roundTrip, "pattern did not survive");
        }

        // A 300x30 surface is the host's default Now-Playing pane size (wmp.dll FUN_18046865c writes
        // 300 x 0x1e into the viz rect at construction), so make sure odd small sizes work too.
        using (var small = new DibTarget(300, 30))
            failures += Check("300x30 surface (the host's default pane size) is creatable",
                small.BitsPerPixel == 32, $"got {small.BitsPerPixel} bpp");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "selftest: PASS" : $"selftest: FAIL ({failures} check(s))");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}{(ok ? "" : $" — {detail}")}");
        return ok ? 0 : 1;
    }
}
