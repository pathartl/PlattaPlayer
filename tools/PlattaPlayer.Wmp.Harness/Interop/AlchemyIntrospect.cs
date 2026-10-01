namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// Reads and steers the real Alchemy's effect scheduler through its own memory, so a SINGLE effect can
/// be held on screen and compared against ours.
///
/// Alchemy offers no way to ask for a particular effect — it picks one at random and swaps it out on a
/// timer — which makes a like-for-like graphical comparison impossible by default. Pinning the
/// scheduler's selection turns it into an effect viewer: hold entry N, capture it, hold OUR entry N,
/// and diff the two.
///
/// Layout, from the decompile:
/// <list type="bullet">
/// <item>The allocation from <c>FUN_180004064</c> constructs CToleranceVis at +0x10, and the
/// IWMPEffects vptr sits at allocation+0xD0 — i.e. <c>CToleranceVis + 0xC0</c>. Render confirms it:
/// its <c>this</c> is used as <c>param_1 - 0xC0</c> to reach the object.</item>
/// <item>The scheduler is the member at <c>CToleranceVis + 0xD48</c> (constructed there by
/// <c>FUN_18000971c</c> and passed to <c>FUN_18000a394</c> each advance).</item>
/// <item>Within it: <c>+0x20</c> is the frame countdown, <c>+0x24</c> the selected entry index, and
/// <c>+0x38</c> the entry count.</item>
/// </list>
/// The offsets are sanity-checked via <see cref="LooksValid"/> before anything is written.
/// </summary>
internal sealed unsafe class AlchemyIntrospect(nint effectsInterface)
{
    private const int InterfaceToObject = 0xC0;
    private const int SchedulerOffset = 0xD48;
    private const int RemainingOffset = 0x20;
    private const int CurrentOffset = 0x24;
    private const int CountOffset = 0x38;

    private readonly nint _scheduler = effectsInterface - InterfaceToObject + SchedulerOffset;

    public int Remaining
    {
        get => *(int*)(_scheduler + RemainingOffset);
        set => *(int*)(_scheduler + RemainingOffset) = value;
    }

    public int CurrentEntry
    {
        get => *(int*)(_scheduler + CurrentOffset);
        set => *(int*)(_scheduler + CurrentOffset) = value;
    }

    public int EntryCount => *(int*)(_scheduler + CountOffset);

    /// <summary>
    /// There is exactly ONE top-level group (<c>FUN_180009a94</c> creates a single one via
    /// <c>FUN_180009d38</c> and then adds eight SUB-slots to it), so a count of 1 with a current index
    /// of 0 is the expected, healthy state. Anything else means the offsets are wrong.
    /// </summary>
    public bool LooksValid => EntryCount == 1 && CurrentEntry == 0 && Remaining > 0;

    /// <summary>
    /// Holds the scheduler on <paramref name="entry"/>. Call before each Render: the advance decrements
    /// the countdown and only re-selects when it expires, so a large countdown keeps the choice fixed.
    /// </summary>
    public void Pin(int entry)
    {
        CurrentEntry = entry;
        Remaining = int.MaxValue / 2;
    }

    public string Describe() =>
        $"scheduler at 0x{_scheduler:X}: entries={EntryCount} current={CurrentEntry} remaining={Remaining}";

    // Pool of registered effects: data pointer and count (the collection at scheduler+0x08).
    private const int PoolDataOffset = 0x10;
    private const int PoolCountOffset = 0x18;

    // The group array (collection at scheduler+0x28) and, within a group, its sub-slots.
    private const int GroupDataOffset = 0x30;
    private const int GroupSlotsOffset = 0x18;
    private const int GroupSlotCountOffset = 0x20;
    private const int SlotStride = 0x38;

    // Per pooled effect.
    private const int EffectWeightOffset = 0x20;
    private const int EffectCategoryOffset = 0x48;

    /// <summary>
    /// Each effect stores the string-resource id of its display name at +0x34, which identifies it
    /// outright — no guessing from screenshots. The strings live in mpvis.dll.mui.
    /// </summary>
    private const int EffectNameIdOffset = 0x34;

    private static readonly Dictionary<int, string> EffectNames = new()
    {
        [100] = "Alchemy",
        [101] = "Blur",
        [102] = "SwitchBlur",
        [103] = "Shift",
        [104] = "Standard Render Cycle",
        [105] = "Linear Shift",
        [106] = "Stretch Shift",
        [107] = "SuperStar",
        [108] = "WonderWave",
        [109] = "Shift O' Scope",
        [110] = "Funktional",
        [115] = "Bass Bounce",
        [117] = "Random",
    };

    public static string NameOf(int nameId) =>
        EffectNames.TryGetValue(nameId, out var n) ? n : $"<id {nameId}>";

    /// <summary>One registered effect: the category a slot must ask for, and its selection weight.</summary>
    public readonly record struct PoolEntry(int Index, int Category, float Weight, nint VTable, int NameId)
    {
        public string Name => NameOf(NameId);
    }

    /// <summary>
    /// One scheduler slot. <c>Category</c> is matched against a pooled effect's category; the slot
    /// activates between <c>MinCount</c> and <c>MaxCount</c> of them (further capped by
    /// <c>Flags</c>: bit 1 =&gt; 10, bit 0 =&gt; 3, otherwise 1) and holds them for a lifetime drawn from
    /// <c>MinLifetime</c>..<c>MaxLifetime</c> frames.
    /// </summary>
    public readonly record struct Slot(
        int Index, int Category, int MinCount, int MaxCount, int MinLifetime, int MaxLifetime, int Flags,
        int ActiveCount, int Countdown);

    /// <summary>
    /// Forces the real effect to render exactly ONE pooled renderer, so it can be compared against the
    /// equivalent of ours.
    ///
    /// Alchemy has no API for this — it picks effects at random. But a slot only accepts a candidate if
    /// <c>rand01 &lt;= weight</c>, so zeroing every other entry's weight makes the choice deterministic.
    /// The remaining slots are pointed at a category with nothing registered (6, which is empty in this
    /// build) so they stay idle, and the surviving slot is given a lifetime long enough that it never
    /// re-rolls. The warp slot (category 3) is left alone: without it the field never transforms.
    /// </summary>
    public void IsolateRenderer(int poolIndex)
    {
        var pool = ReadPool();
        for (var i = 0; i < pool.Length; i++)
            SetPoolWeight(i, i == poolIndex ? 1.0f : 0.0f);

        var slots = ReadSlots();
        var kept = false;
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].Category == 3) continue;              // the warp stage stays

            if (!kept && slots[i].Category == pool[poolIndex].Category)
            {
                ConfigureSlot(i, slots[i].Category, 1, 1, int.MaxValue / 4);
                kept = true;
            }
            else
            {
                ConfigureSlot(i, EmptyCategory, 0, 0, int.MaxValue / 4);
            }
        }
        ForceReroll();
    }

    /// <summary>A category with no registered effects in this build, so a slot pointed at it stays idle.</summary>
    private const int EmptyCategory = 6;

    public void SetPoolWeight(int index, float weight)
    {
        var data = *(nint*)(_scheduler + PoolDataOffset);
        var effect = *(nint*)(data + index * sizeof(nint));
        *(float*)(effect + EffectWeightOffset) = weight;
    }

    public void ConfigureSlot(int index, int category, int minCount, int maxCount, int lifetime)
    {
        var slot = SlotAddress(index);
        *(int*)slot = category;
        *(int*)(slot + 4) = minCount;
        *(int*)(slot + 8) = maxCount;
        *(int*)(slot + 0x0C) = lifetime;
        *(int*)(slot + 0x10) = lifetime;
    }

    /// <summary>Zeroes every slot's countdown so the next advance re-rolls them all.</summary>
    public void ForceReroll()
    {
        var slots = ReadSlots();
        for (var i = 0; i < slots.Length; i++) *(int*)(SlotAddress(i) + 0x30) = 0;
    }

    private nint SlotAddress(int index)
    {
        var groups = *(nint*)(_scheduler + GroupDataOffset);
        var group = *(nint*)(groups + CurrentEntry * sizeof(nint));
        return *(nint*)(group + GroupSlotsOffset) + index * SlotStride;
    }

    public PoolEntry[] ReadPool()
    {
        var data = *(nint*)(_scheduler + PoolDataOffset);
        var count = *(int*)(_scheduler + PoolCountOffset);
        if (data == 0 || count is < 0 or > 256) return [];

        var pool = new PoolEntry[count];
        for (var i = 0; i < count; i++)
        {
            var effect = *(nint*)(data + i * sizeof(nint));
            pool[i] = new PoolEntry(
                i,
                *(int*)(effect + EffectCategoryOffset),
                *(float*)(effect + EffectWeightOffset),
                *(nint*)effect,
                *(int*)(effect + EffectNameIdOffset));
        }
        return pool;
    }

    public Slot[] ReadSlots()
    {
        var groups = *(nint*)(_scheduler + GroupDataOffset);
        if (groups == 0 || EntryCount < 1) return [];

        var group = *(nint*)(groups + CurrentEntry * sizeof(nint));
        var slots = *(nint*)(group + GroupSlotsOffset);
        var count = *(int*)(group + GroupSlotCountOffset);
        if (slots == 0 || count is < 0 or > 64) return [];

        var result = new Slot[count];
        for (var i = 0; i < count; i++)
        {
            var s = slots + i * SlotStride;
            result[i] = new Slot(
                i,
                *(int*)s, *(int*)(s + 4), *(int*)(s + 8),
                *(int*)(s + 0x0C), *(int*)(s + 0x10), *(int*)(s + 0x14),
                *(int*)(s + 0x28), *(int*)(s + 0x30));
        }
        return result;
    }

    /// <summary>The pool indices slot <paramref name="index"/> currently holds, in acceptance order.</summary>
    public int[] SlotActive(int index)
    {
        var s = SlotAddress(index);
        var count = *(int*)(s + 0x28);
        var data = *(nint*)(s + 0x20);
        var poolData = *(nint*)(_scheduler + PoolDataOffset);
        var poolCount = *(int*)(_scheduler + PoolCountOffset);
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var effect = *(nint*)(data + i * sizeof(nint));
            result[i] = -1;
            for (var p = 0; p < poolCount; p++)
                if (*(nint*)(poolData + p * sizeof(nint)) == effect) result[i] = p;
        }
        return result;
    }
}
