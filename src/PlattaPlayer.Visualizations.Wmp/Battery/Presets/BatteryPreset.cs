using System.Globalization;
using PlattaPlayer.Visualizations.Wmp.Battery.Effects;
using PlattaPlayer.Visualizations.Wmp.Battery.Shifts;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Presets;

/// <summary>
/// <c>CBaseEffect</c>, one entry in Battery's preset list. Each frame it ticks the reroll countdowns,
/// builds three more rows of the pending warp, then runs the pipeline: pre effects, the warp gather,
/// post effects, and the blur that is also the fade.
///
/// <see cref="RandomPreset"/> ("Randomization", index 0) rerolls the shift every 96..485 frames and the
/// effects every 90..399. <see cref="SavedPreset"/> plays a fixed registry recipe and never draws.
/// Derivation: <c>Code\battery\01_control.md</c> §4, <c>02_pipeline.md</c> §2–3.
/// </summary>
public abstract class BatteryPreset(BatteryRenderData rd)
{
    protected BatteryRenderData Rd { get; } = rd;

    public List<RenderEffect> Pre { get; } = [];

    public List<RenderEffect> Post { get; } = [];

    /// <summary>+0x630: the shift on screen.</summary>
    public ShiftTable? Current { get; protected set; }

    /// <summary>+0x638: the shift being built in the background.</summary>
    public ShiftTable? Next { get; private set; }

    /// <summary>CShiftTransform's own table (+0x20). It is set together with <see cref="Current"/>.</summary>
    public ShiftTable? TransformTable { get; protected set; }

    public int W { get; private set; }

    public int H { get; private set; }

    protected bool RerollEffects { get; init; } = true;

    protected bool RerollShift { get; init; } = true;

    public int EffectsCountdown { get; private set; }

    public int ShiftCountdown { get; private set; }

    public bool Paused { get; protected set; }

    /// <summary>+0x688: this preset's palette-lock flag. SetSize copies it into the render data.</summary>
    protected bool LockFlag { get; set; }

    public abstract string Title { get; }

    /// <summary>Slot 3 (<c>0x180414540</c>): zero the size and both countdowns. Random mode therefore
    /// rerolls everything on its first frame.</summary>
    public virtual void Allocate(CrtRand rand)
    {
        W = H = 0;
        EffectsCountdown = ShiftCountdown = 0;
    }

    /// <summary>Slot 4 (<c>0x180414780</c>).</summary>
    public virtual void Free()
    {
        if (!RerollShift) return;
        TransformTable = null;
        Current = null;
        Next = null;
    }

    /// <summary>Slot 5 (<c>0x1804160b0</c>): this goes through EndPresetSave, which copies this preset's
    /// lock flag into the render data. That is how a resize unlocks a locked palette.</summary>
    public virtual void SetSize(int w, int h)
    {
        W = w;
        H = h;
        Rd.Palette.Locked = LockFlag;
        Paused = false;
    }

    /// <summary>Slot 8 (<c>0x1804155a0</c>). The text overlay is omitted: it needs fullscreen and the
    /// <c>Enable</c> registry flag.</summary>
    public void Render(BatteryLevels tl, CrtRand rand)
    {
        if (!Paused)
        {
            ShiftCountdown--;
            EffectsCountdown--;
        }
        if (RerollShift && ShiftCountdown < 1) NewShiftTable(rand);
        if (RerollEffects && EffectsCountdown < 1) NewRenderEffects(rand);
        Next?.Setup(rand);
        BasicRender(tl, rand);
    }

    /// <summary>Slot 10 (<c>0x1804145c0</c>).</summary>
    private void BasicRender(BatteryLevels tl, CrtRand rand)
    {
        var mask = Current?.Flags ?? 0;
        foreach (var e in Pre)
            if ((mask & e.Flags) == 0) e.Render(tl, Rd, rand);

        ShiftTransform(rand);

        foreach (var e in Post) e.Render(tl, Rd, rand);

        Rd.Raster.Blur();
    }

    /// <summary><c>CShiftTransform::Perform</c> (<c>0x180412460</c>): finish the table if it was not
    /// finished in time, swap, and gather.</summary>
    private void ShiftTransform(CrtRand rand)
    {
        var table = TransformTable;
        if (table is null) return;
        table.Complete(Rd.W, Rd.H, rand);
        if (table.Table is null) return;
        Rd.Raster.Gather(table, table.GetData(rand)!);
    }

    /// <summary>Slot 13 (<c>0x180414ea0</c>).</summary>
    protected void NewShiftTable(CrtRand rand)
    {
        var b = H / 3;
        if (b < 60) b = 60;
        ShiftCountdown = rand.Next() % 390 + b;
        SelectRandomShiftTable(rand);
    }

    /// <summary>Slot 14 (<c>0x180414e40</c>).</summary>
    private void NewRenderEffects(CrtRand rand)
    {
        EffectsCountdown = rand.Next() % 310 + 90;
        SelectRandomRenderEffects(rand);
    }

    /// <summary>Slot 12 (<c>0x180415f40</c>). On the very first call it recurses, so that a current and
    /// a pending shift both exist.</summary>
    private void SelectRandomShiftTable(CrtRand rand)
    {
        Current?.SetSize(0, 0, false);
        Current = Next;
        Next = null;
        TransformTable = Current;

        var pool = Rd.Shifts;
        if (pool.Length <= 0) return;
        do
        {
            Next = pool[rand.Next() % pool.Length];
        } while (Next == Current && pool.Length > 1);

        Next.SetSize(Rd.W, Rd.H, Rd.UseTransitions);
        Next.SetRandom(rand);

        if (Current is null) SelectRandomShiftTable(rand);
        else Next.SetLastShift(Current);
    }

    /// <summary>Slot 11 (<c>0x180415d60</c>). Effects are shared pool singletons, so one can land in a
    /// list twice and render twice with its second Randomize's parameters.</summary>
    private void SelectRandomRenderEffects(CrtRand rand)
    {
        Pre.Clear();
        Post.Clear();
        var pool = Rd.Effects;

        var k = rand.Next() % 2;
        while (k > 0)
        {
            var e = pool[rand.Next() % pool.Length];
            if ((e.Flags & 1) == 0) continue;
            e.SetRandom(rand);
            Pre.Add(e);
            k--;
        }

        k = (rand.Next() % 4 + 1) / 4 + 1;
        while (k > 0)
        {
            var e = pool[rand.Next() % pool.Length];
            if ((e.Flags & 2) == 0) continue;
            e.SetRandom(rand);
            var r = rand.Next();
            // The shift's +0xe4 "post bias" would draw rand() % 6 here; it is always 0 on x64.
            ((r & 1) != 0 ? Pre : Post).Add(e);
            k--;
        }
    }
}

/// <summary><c>CRandomEffect</c>: preset 0, "Randomization".</summary>
public sealed class RandomPreset(BatteryRenderData rd) : BatteryPreset(rd)
{
    public override string Title => "Randomization";
}

/// <summary>
/// <c>CSavedEffect</c>: one registry recipe. Its pre/post lists, its shift and their dbl1..dbl8 are
/// loaded on Allocate (<c>LoadSettings</c>, <c>0x1804149c0</c>), and it never calls Randomize. A
/// PaletteLocked recipe also loads its 256 × RGBX palette into the target and fades it in.
/// </summary>
public sealed class SavedPreset : BatteryPreset
{
    private readonly RegistryKeyData _recipe;

    public SavedPreset(BatteryRenderData rd, RegistryKeyData recipe) : base(rd)
    {
        _recipe = recipe;
        RerollEffects = false;
        RerollShift = false;
        Paused = true;
        TitleResource = recipe.String("Title") ?? recipe.Name;
    }

    /// <summary>The registry key name, e.g. <c>circledance</c>.</summary>
    public string Key => _recipe.Name;

    /// <summary>The <c>Title</c> value, e.g. <c>res://wmploc/RT_STRING/#5721</c>.</summary>
    public string TitleResource { get; }

    public override string Title => PresetTitles.Resolve(TitleResource);

    public override void Allocate(CrtRand rand)
    {
        base.Allocate(rand);
        LoadSettings(rand);
    }

    public override void SetSize(int w, int h)
    {
        var locked = Rd.Palette.Locked; // CSavedEffect::SetSize restores the lock around the base call
        base.SetSize(w, h);
        Rd.Palette.Locked = locked;
    }

    private void LoadSettings(CrtRand rand)
    {
        Pre.Clear();
        var pre = (int)(_recipe.DWord("PreShiftCount") ?? 0);
        for (var i = 0; i < pre; i++)
        {
            var e = Rd.FindEffect(_recipe.String($"PreShift{i}") ?? "");
            if (e is null) continue;
            Load(e, "PreShiftInfo", i);
            Pre.Add(e);
        }

        Post.Clear();
        var post = (int)(_recipe.DWord("PostShiftCount") ?? 0);
        for (var i = 0; i < post; i++)
        {
            var e = Rd.FindEffect(_recipe.String($"PostShift{i}") ?? "");
            if (e is null) continue;
            Load(e, "PostShiftInfo", i);
            Post.Add(e);
        }

        var s = Rd.FindShift(_recipe.String("CurrentShift") ?? "");
        if (s is not null)
        {
            Load(s, "CurrentShiftInfo", 0);
            Current = s;
            TransformTable = s;
        }
        else
        {
            NewShiftTable(rand);
        }

        var locked = (_recipe.DWord("PaletteLocked") ?? 0) != 0;
        Rd.Palette.Locked = locked;
        if (!locked) return;
        if (_recipe.Binary("Palette") is { } bytes)
        {
            for (var i = 0; i < 256 && 4 * i + 3 < Math.Min(bytes.Length, 0x400); i++)
                Rd.Palette.Target[i] = BitConverter.ToUInt32(bytes, 4 * i);
        }
        Rd.Palette.PendingLocked = true;
    }

    /// <summary><c>CMemoryEffect::Load</c> (<c>0x180411dc8</c>). Each <c>dblN</c> is an en-US string
    /// (VarR8FromStr, which matches .NET's correctly rounded parse on every shipped value). A missing or
    /// unparsable value leaves the field as it was. Then MarkDirty runs.</summary>
    private void Load(MemoryEffect e, string infoKey, int index)
    {
        var key = _recipe.SubKey(infoKey)?.SubKey(index.ToString(CultureInfo.InvariantCulture));
        if (key is null) return;
        for (var j = 0; j < 8; j++)
        {
            var s = key.String($"dbl{j + 1}");
            if (!string.IsNullOrEmpty(s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                e.P[j] = v;
        }
        e.MarkChanged();
    }
}

/// <summary>The English display titles of the shipped presets: wmploc.dll's RT_STRING table, which
/// WMP resolves from each recipe's <c>Title</c> value.</summary>
public static class PresetTitles
{
    private static readonly Dictionary<int, string> Table = new()
    {
        [5700] = "brightsphere", [5701] = "cominatcha", [5702] = "dandelionaid", [5703] = "drinkdeep",
        [5704] = "eletriarnation", [5705] = "cottonstar", [5706] = "gemstonematrix", [5707] = "sepiaswirl",
        [5708] = "event horizon", [5709] = "illuminator", [5710] = "i see the truth", [5711] = "kaleidovision",
        [5712] = "green is not your enemy", [5713] = "lotus", [5714] = "relatively calm", [5715] = "sleepyspray",
        [5716] = "smoke or water?", [5717] = "back to the groove", [5718] = "spider's last moment...",
        [5719] = "strawberryaid", [5720] = "the world", [5721] = "dance of the freaky circles",
        [5722] = "my tornado is resting", [5723] = "hizodge", [5724] = "chemicalnova",
    };

    public static string Resolve(string title)
    {
        var hash = title.LastIndexOf('#');
        return title.StartsWith("res://", StringComparison.OrdinalIgnoreCase) && hash >= 0 &&
               int.TryParse(title.AsSpan(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
               Table.TryGetValue(id, out var text)
            ? text
            : title;
    }
}
