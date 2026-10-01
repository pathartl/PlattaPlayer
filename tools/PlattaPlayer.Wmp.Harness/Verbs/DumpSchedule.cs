using PlattaPlayer.Wmp.Harness.Interop;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Dumps the real Alchemy's scheduler wiring from a live object: the pool of registered effects (each
/// with its category and selection weight) and the eight slots that draw from it.
///
/// This is what makes an eight-slot reimplementation possible without guessing. The slot configs are
/// visible in the decompile (<c>FUN_180009e48</c>), but which POOLED EFFECT each slot's category refers
/// to is not — the category is written by each effect's constructor. Reading both from the running
/// object gives the mapping directly.
/// </summary>
internal static class DumpSchedule
{
    public static int Run(string[] args)
    {
        var seed = args.Contains("--seed") ? uint.Parse(args[Array.IndexOf(args, "--seed") + 1]) : 1u;

        using var fx = AlchemyFactory.CreateSeeded(seed);
        using var dib = new DibTarget(640, 480);
        using var levels = new TimedLevelBuffer();

        // Advance a few frames so every slot has rolled at least once and populated its active list.
        for (var b = 0; b < 96; b++) { levels.Frequency0[b] = 200; levels.Frequency1[b] = 200; }
        levels.State = 2;
        var rc = new RECT(0, 0, 640, 480);
        for (var i = 0; i < 8; i++)
        {
            levels.TimeStamp = i * 10_000_000L / 60;
            unsafe { fx.Render(levels.Pointer, dib.Hdc, ref rc); }
            VirtualClock.AdvanceFrame();
        }

        var scheduler = new AlchemyIntrospect(fx.RawPointer);
        Console.WriteLine(scheduler.Describe());
        if (!scheduler.LooksValid)
        {
            Console.Error.WriteLine("scheduler offsets do not look right; refusing to interpret.");
            return 1;
        }

        var pool = scheduler.ReadPool();
        Console.WriteLine();
        Console.WriteLine($"POOL — {pool.Length} registered effect(s)");
        Console.WriteLine("   idx  category  weight   name");
        foreach (var e in pool)
            Console.WriteLine($"   {e.Index,3}  {e.Category,8}  {e.Weight,6:F2}   {e.Name}");

        var slots = scheduler.ReadSlots();
        Console.WriteLine();
        Console.WriteLine($"SLOTS — {slots.Length}, all advanced every frame");
        Console.WriteLine("   idx  category  count      lifetime     flags  cap  active  countdown  eligible");
        foreach (var s in slots)
        {
            var cap = (s.Flags & 2) != 0 ? 10 : (s.Flags & 1) != 0 ? 3 : 1;
            var eligible = pool.Where(p => p.Category == s.Category).Select(p => p.Name).ToList();
            Console.WriteLine(
                $"   {s.Index,3}  {s.Category,8}  {s.MinCount}..{s.MaxCount,-6} " +
                $"{s.MinLifetime,4}..{s.MaxLifetime,-4}  {s.Flags,5}  {cap,3}  {s.ActiveCount,6}  " +
                $"{s.Countdown,9}  [{string.Join(",", eligible)}]");
        }

        Console.WriteLine();
        Console.WriteLine("A slot activates between count.min and count.max pooled effects whose category matches,");
        Console.WriteLine("capped by flags, each accepted with probability = its weight, then holds them for a");
        Console.WriteLine("lifetime drawn from the frame range. All slots run concurrently every frame.");
        return 0;
    }
}
