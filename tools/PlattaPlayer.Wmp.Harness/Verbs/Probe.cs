using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Identifies the three real WMP visualizations and proves we can instantiate each one.
///
/// This is the HARD GATE for the whole rework: everything downstream compares our renderers against
/// frames these objects produce, so if the identification is wrong the comparison is worthless. The
/// proof is not the pointer arithmetic in <see cref="WmpInternalFactory"/> — it is that each object
/// reports the preset list the decompile says it should.
/// </summary>
internal static class Probe
{
    private static readonly string[] ExpectedBarsPresets = ["Bars", "Ocean Mist", "Fire Storm", "Scope"];

    public static int Run(string[] args)
    {
        var failures = 0;

        Console.WriteLine("== Alchemy (mpvis.DLL, registered COM server) ==");
        failures += ProbeAlchemy();

        Console.WriteLine();
        Console.WriteLine("== wmp.dll (internal, unregistered) ==");
        WmpInternalFactory.Load();
        Console.Write(WmpInternalFactory.DescribeModule());

        Console.WriteLine();
        Console.WriteLine("-- Bars and Waves --");
        failures += ProbeInternal(WmpGuids.ClsidBarsAndWaves, 4, ExpectedBarsPresets);

        Console.WriteLine();
        Console.WriteLine("-- Battery --");
        // 26, not 25: index 0 is a "Randomization" meta-preset that cycles the other 25. The earlier
        // port counted only the 25 named recipes under HKLM\...\Battery\Presets and so was off by one
        // for every index. See CompareBatteryToRegistry for the name mismatch this also exposed.
        failures += ProbeInternal(WmpGuids.ClsidBattery, 26, null);
        CompareBatteryToRegistry();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "probe: PASS — ground truth is available for all three effects."
            : $"probe: FAIL ({failures} check(s)) — do NOT trust any capture until this passes.");
        return failures == 0 ? 0 : 1;
    }

    private static int ProbeAlchemy()
    {
        var failures = 0;
        try
        {
            using var fx = AlchemyFactory.Create();

            var title = fx.GetTitle();
            failures += Check($"GetTitle() == \"Alchemy\"", title == "Alchemy", $"got \"{title}\"");

            var count = fx.GetPresetCount();
            failures += Check("GetPresetCount() == 1", count == 1, $"got {count}");

            var preset = fx.GetPresetTitle(0);
            failures += Check("GetPresetTitle(0) == \"Random\"", preset == "Random", $"got \"{preset}\"");

            // SetCurrentPreset is literally `xor eax,eax; ret` in this build — it must succeed and
            // change nothing. If a future build ever implements it, this is where we find out.
            var hr = fx.SetCurrentPreset(5);
            var current = fx.GetCurrentPreset();
            failures += Check("SetCurrentPreset(5) is a no-op returning S_OK",
                hr == 0 && current == 0, $"hr=0x{hr:X8}, GetCurrentPreset()={current}");

            var caps = fx.GetCapabilities();
            Console.WriteLine($"  [info] GetCapabilities() = 0x{caps:X8} (0 = declares nothing, incl. no fullscreen)");

            // Render once so the scheduler has actually selected something, then check the introspection
            // offsets before any capture relies on them.
            using (var dib = new DibTarget(640, 480))
            using (var levels = new TimedLevelBuffer())
            {
                for (var b = 0; b < 64; b++) { levels.Frequency0[b] = 200; levels.Frequency1[b] = 200; }
                levels.State = 2;
                var rc = new RECT(0, 0, 640, 480);
                unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
            }

            var scheduler = new AlchemyIntrospect(fx.RawPointer);
            Console.WriteLine($"  [info] {scheduler.Describe()}");
            failures += Check("scheduler introspection offsets resolve (1 group, 8 sub-slots inside it)",
                scheduler.LooksValid, "offsets do not look right");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] could not create Alchemy — {ex.Message}");
            failures++;
        }
        return failures;
    }

    private static int ProbeInternal(Guid clsid, int expectedPresetCount, string[]? expectedTitles)
    {
        var failures = 0;
        try
        {
            var factory = WmpInternalFactory.Resolve(clsid);
            Console.WriteLine($"  clsid bytes    0x{factory.ClsidAddress:X}");
            Console.WriteLine($"  table entry    0x{factory.TableEntry:X}");
            Console.WriteLine($"  createFn       0x{factory.CreateFn:X}  ({WmpInternalFactory.ReferenceDrift(clsid, factory.CreateFn)})");

            using var fx = WmpInternalFactory.CreateInstance(factory);

            var title = fx.GetTitle();
            Console.WriteLine($"  GetTitle()     \"{title}\"");

            var count = fx.GetPresetCount();
            failures += Check($"GetPresetCount() == {expectedPresetCount}",
                count == expectedPresetCount, $"got {count}");

            var titles = new List<string>();
            for (var i = 0; i < count; i++)
            {
                try { titles.Add(fx.GetPresetTitle(i)); }
                catch (Exception ex) { titles.Add($"<error: {ex.Message}>"); }
            }
            for (var i = 0; i < titles.Count; i++)
                Console.WriteLine($"    preset {i,2}    \"{titles[i]}\"");

            if (expectedTitles is not null)
                failures += Check("preset titles match the decompile order",
                    titles.SequenceEqual(expectedTitles),
                    $"got [{string.Join(", ", titles)}]");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        return failures;
    }

    /// <summary>
    /// Battery's per-preset recipes live in the registry, but the registry KEY names are not the
    /// user-visible titles (e.g. key "circledance" is shown as "dance of the freaky circles"). The port
    /// used the key names as display names, so this prints the correspondence and confirms whether
    /// registry enumeration order matches IWMPEffects preset order — the plan flagged a silent
    /// off-by-order here as the failure that would render every preset wrong under the right name.
    /// </summary>
    private static void CompareBatteryToRegistry()
    {
        const string path = @"SOFTWARE\Microsoft\MediaPlayer\Battery\Presets";
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
        if (key is null)
        {
            Console.WriteLine($"  [info] HKLM\\{path} not present — cannot cross-check preset order.");
            return;
        }

        var names = key.GetSubKeyNames();
        Console.WriteLine($"  [info] HKLM\\{path} has {names.Length} recipe key(s):");
        for (var i = 0; i < names.Length; i++)
            Console.WriteLine($"    registry {i,2}  \"{names[i]}\"  (would be preset index {i + 1} if index 0 is Randomization)");
    }

    private static int Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}{(ok ? "" : $" — {detail}")}");
        return ok ? 0 : 1;
    }
}
