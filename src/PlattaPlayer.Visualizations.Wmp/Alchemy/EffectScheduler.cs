using System;
using System.Collections.Generic;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Effects;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// Chooses and runs the active Alchemy effects. This is a line-for-line model of
/// <c>CToleranceEffects::Render</c> (<c>0x18000a394</c>), <c>CToleranceRenderStep::Render</c>
/// (<c>0x18000a4f4</c>) and <c>CToleranceRenderStep::SelectEffects</c> (<c>0x18000a624</c>), and it is
/// checked against them by the harness verb <c>verify-scheduler</c>.
///
/// The POOL is built once by <c>CToleranceEffects::AddEffects</c> (<c>0x180009a94</c>). Each entry has a
/// category (effect <c>+0x48</c>) and a float weight (<c>+0x20</c>):
/// <code>
///   0  cat 3  1.0   Shift (the warp stage)      4  cat 4  0.5   SuperStar
///   1  cat 2  1.0   Blur                        5  cat 7  0.5   Bass Bounce
///   2  cat 2  0.2   SwitchBlur                  6  cat 4  0.9   AtomBalls
///   3  cat 4  1.0   WonderWave
/// </code>
/// One render cycle (1500..7500 frames, weight 1.0) holds EIGHT steps, run in order every frame:
/// <code>
///   step  cat  count  lifetime  flags (k)
///     0    3   1..1    90..500   0 (1)     the warp stage
///     1    4   1..2    50..350   2 (10)    renderers
///     2    6   0..1    30..500   1 (3)     \
///     3    5   0..4    30..500   1 (3)      | no effect of category 5 or 6 exists, so these never
///     4    6   0..1    30..500   1 (3)      | hold anything, but they still DRAW when they re-roll
///     5    4   1..3    50..350   2 (10)    renderers
///     6    5   0..2    30..500   1 (3)     /
///     7    7   0..1    50..400   1 (3)     bass bounce
/// </code>
/// Each step has its OWN countdown and re-rolls on its own. On a re-roll it releases the effects it held,
/// draws a count (the minimum of k draws, <see cref="MpvisMath.MinOfK"/>) and then a lifetime
/// (<see cref="MpvisMath.RandLong"/>, inclusive), and makes up to 100 attempts to fill itself. Attempt
/// <c>i</c> runs from 99 down to 0. When <c>i</c> is at least the pool size it draws an index, and below
/// that it uses <c>i</c> itself, so the last seven attempts sweep the pool deterministically. A candidate
/// must match the category and not be held by any step (<c>+0x24</c>). Only then is the weight test
/// <c>rand()/32767.0 &lt;= weight</c> drawn. An accepted effect is randomized, and its fade envelope
/// is set from a DOUBLE-EVALUATED min macro:
/// <c>r = RandLong(10,90); dur = r &lt; life/3 ? RandLong(10,90) : life/3</c>. The second call is a fresh
/// draw, not <c>r</c>.
///
/// Things the previous version got wrong, all fixed here: it re-randomized every live effect on each bass
/// hit (the original never does — only the 'M' key re-rolls anything outside the schedule); it re-rolled
/// both renderer slots whenever either expired; its lifetimes excluded the maximum; it drew the lifetime
/// before the count; it took a single-draw min for the fade; it skipped the four dead steps' draws; and it
/// built fresh renderer objects on every selection instead of re-randomizing persistent ones.
/// </summary>
public sealed class EffectScheduler
{
    /// <summary>An entry in the effect pool.</summary>
    public sealed class PoolEntry(AlchemyEffect effect, int category, float weight)
    {
        public AlchemyEffect Effect { get; } = effect;

        /// <summary>Effect <c>+0x48</c>: which steps may hold it.</summary>
        public int Category { get; } = category;

        /// <summary>Effect <c>+0x20</c>, a FLOAT, compared after widening to double.</summary>
        public float Weight { get; } = weight;

        /// <summary>Effect <c>+0x24</c>: held by some step.</summary>
        public bool Taken { get; internal set; }
    }

    /// <summary>A <c>CToleranceRenderStep</c> (0x38 bytes).</summary>
    public sealed class RenderStep(int category, int minCount, int maxCount, int minLifetime, int maxLifetime, int flags)
    {
        private readonly List<PoolEntry> _active = [];

        public int Category { get; } = category;          // +0x00
        public int MinCount { get; } = minCount;          // +0x04
        public int MaxCount { get; } = maxCount;          // +0x08
        public int MinLifetime { get; } = minLifetime;    // +0x0c
        public int MaxLifetime { get; } = maxLifetime;    // +0x10
        public int Flags { get; } = flags;                // +0x14

        /// <summary>+0x30: frames until this step re-rolls.</summary>
        public int Countdown { get; internal set; }

        /// <summary>+0x18: the effects this step currently holds, in acceptance order.</summary>
        public IReadOnlyList<PoolEntry> Active => _active;

        /// <summary>The k of the count draw: 10 for flag bit 1, else 3 for bit 0, else 1.</summary>
        public int CountDraws => (Flags & 2) != 0 ? 10 : ((Flags & 1) * 2) | 1;

        internal List<PoolEntry> MutableActive => _active;
    }

    /// <summary>The shipped step table, from <c>AddEffects</c>.</summary>
    private static readonly (int Category, int Min, int Max, int MinLife, int MaxLife, int Flags)[] StepTable =
    [
        (3, 1, 1, 90, 500, 0),
        (4, 1, 2, 50, 350, 2),
        (6, 0, 1, 30, 500, 1),
        (5, 0, 4, 30, 500, 1),
        (6, 0, 1, 30, 500, 1),
        (4, 1, 3, 50, 350, 2),
        (5, 0, 2, 30, 500, 1),
        (7, 0, 1, 50, 400, 1),
    ];

    private const int CategoryShift = 3;
    private const int CategoryRender = 4;
    private const int CategoryBassBounce = 7;

    /// <summary>The single render cycle's duration range, set by <c>AddRenderCycle</c>.</summary>
    private const int CycleMinFrames = 1500;
    private const int CycleMaxFrames = 7500;

    private readonly Random _random;
    private readonly PoolEntry[] _pool;
    private readonly RenderStep[] _steps;
    private readonly PoolEntry?[] _drawEntries;
    private readonly List<AlchemyEffect> _draws = [];
    private int _cycleCountdown;

    /// <summary>The shipped configuration.</summary>
    public EffectScheduler(EffectRegistry registry, Random random)
        : this(BuildPool(registry, random, out var drawEntries), random)
    {
        _drawEntries = drawEntries;
        Stage = (WarpStage)_pool[0].Effect;
    }

    /// <summary>
    /// An arbitrary pool with the shipped step table — how the harness drives this against the real
    /// <c>SelectEffects</c> with stand-in effects.
    /// </summary>
    public EffectScheduler(IReadOnlyList<PoolEntry> pool, Random random)
    {
        _random = random;
        _pool = [.. pool];
        _steps = Array.ConvertAll(StepTable, s => new RenderStep(s.Category, s.Min, s.Max, s.MinLife, s.MaxLife, s.Flags));
        _drawEntries = [];
    }

    private static PoolEntry[] BuildPool(EffectRegistry registry, Random random, out PoolEntry?[] drawEntries)
    {
        // Built in AddEffects' order, because the constructors draw: the stage's OScope kernels first,
        // then WonderWave (24 draws), SuperStar (30) and AtomBalls (30). BassBounce and the blurs draw
        // nothing. Harness probe-ctors measures these counts.
        var stage = new PoolEntry(registry.CreateWarpStage(random), CategoryShift, 1.0f);
        var wonderWave = new PoolEntry(registry.NewDraw(2, random), CategoryRender, 1.0f);
        var superStar = new PoolEntry(registry.NewDraw(0, random), CategoryRender, 0.5f);
        var atomBalls = new PoolEntry(registry.NewDraw(1, random), CategoryRender, 0.9f);
        drawEntries = [superStar, atomBalls, wonderWave]; // registry order, for PinnedDraw

        return
        [
            stage,
            new PoolEntry(new InertEffect("Blur"), 2, 1.0f),
            new PoolEntry(new InertEffect("SwitchBlur"), 2, 0.2f),
            wonderWave,
            superStar,
            new PoolEntry(registry.NewBassBounce(), CategoryBassBounce, 0.5f),
            atomBalls,
        ];
    }

    /// <summary>The pool, in registration order.</summary>
    public IReadOnlyList<PoolEntry> Pool => _pool;

    /// <summary>The eight steps, in the order they run.</summary>
    public IReadOnlyList<RenderStep> Steps => _steps;

    /// <summary>Frames until the render cycle re-rolls (<c>CToleranceEffects +0x20</c>).</summary>
    public int CycleCountdown => _cycleCountdown;

    /// <summary>
    /// <c>renderData +0x11</c>, toggled by the original's SPACE key: while set no countdown runs, so the
    /// current selection holds indefinitely. Nothing in the player sets it; the harness does.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>The warp stage (null only for a harness-supplied pool without one).</summary>
    public WarpStage? Stage { get; }

    /// <summary>The primary warp kernel in force, or null before the first frame.</summary>
    public AlchemyEffect? WarpA => Stage?.CurrentA;

    /// <summary>The stage's optional second kernel (1 in 5 selections), averaged into the warp map.</summary>
    public AlchemyEffect? WarpB => Stage?.CurrentB;

    /// <summary>
    /// The bass bounce if step 7 currently holds it. It is scheduled, randomized and ticked, but it does
    /// NOT reach the image. Its only output is the rect it copies to <c>renderData+0x60</c>, and nothing
    /// in this build reads that rect: <c>SetLevels</c> empties it every frame, and both present paths
    /// (<c>CToleranceSurface::Render</c> via GDI <c>StretchBlt</c>, and the D3D path) pass their own
    /// rects. The port used to fold it into the warp map as a zoom, which also displaced the stage's own
    /// second kernel whenever step 7 held it.
    /// </summary>
    public AlchemyEffect? BassBounce
    {
        get
        {
            foreach (var entry in _steps[^1].Active)
                if (entry.Category == CategoryBassBounce) return entry.Effect;
            return null;
        }
    }

    /// <summary>Every renderer held across the steps, in step order.</summary>
    public IReadOnlyList<AlchemyEffect> Draws => _draws;

    public string CurrentName
    {
        get
        {
            var warp = WarpA?.Name ?? "Shift";
            return _draws.Count == 0 ? warp : $"{warp} + {string.Join(" + ", _draws.ConvertAll(d => d.Name))}";
        }
    }

    /// <summary>Diagnostic: hold one warp kernel TYPE (and suppress the bass bounce).</summary>
    public int? PinnedWarp { get; private set; }

    /// <summary>Diagnostic: hold one renderer, by registry index.</summary>
    public int? PinnedDraw { get; private set; }

    /// <summary>
    /// Diagnostic pinning for the harness. Every step re-selects on the next frame and the warp stage
    /// starts over, so the pins take effect at once.
    /// </summary>
    public void Pin(int? warp, int? draw)
    {
        PinnedWarp = warp;
        PinnedDraw = draw;
        if (Stage is not null)
        {
            Stage.PinnedType = warp;
            Stage.Restart(_random);
        }
        foreach (var step in _steps) step.Countdown = 0;
    }

    /// <summary>
    /// The host's "next preset". The original's nearest equivalent is the 'm' key
    /// (<c>CToleranceVis::OnKey</c> → <c>RandomizeRenderStep</c>), which calls <c>ForceRandomize(0)</c> on
    /// whatever the category-3 step holds — a new warp kernel, bypassing the build/morph gate.
    /// </summary>
    public void Reshuffle() => Stage?.ForceRandomize(_random);

    /// <summary>Set the field size on every pooled effect (the original's resize pass over the pool).</summary>
    public void Resize(int width, int height)
    {
        foreach (var entry in _pool)
        {
            entry.Effect.FieldWidth = width;
            entry.Effect.FieldHeight = height;
        }
        Stage?.ResizeKernels(width, height);
    }

    /// <summary>
    /// One frame of <c>CToleranceEffects::Render</c>. <paramref name="render"/> is called for every held
    /// effect, in step order, exactly where the original calls its <c>NormalRender</c> — so the engine does
    /// its warp, feedback and drawing inside it, and any random draws those make fall in the right place.
    /// </summary>
    public void Run(Action<AlchemyEffect> render)
    {
        if (!Paused) _cycleCountdown--;
        if (_cycleCountdown < 1)
        {
            // There is exactly one cycle, of weight 1.0: the index draw is rand() % 1 and the weight test
            // (int)(1.0 * 1000) < rand() % 1000 never rejects, so this is always two draws and a duration.
            const int cycleCount = 1;
            const double cycleWeight = 1.0;
            do
            {
                _ = _random.Next(cycleCount);
            } while ((int)(cycleWeight * 1000) < _random.Next(1000));
            _cycleCountdown = CycleMinFrames + _random.Next(CycleMaxFrames - CycleMinFrames);
        }

        foreach (var step in _steps) RunStep(step, render);
    }

    /// <summary><c>CToleranceRenderStep::Render</c>.</summary>
    private void RunStep(RenderStep step, Action<AlchemyEffect> render)
    {
        if (!Paused) step.Countdown--;
        if (step.Countdown < 1) Select(step);

        foreach (var entry in step.Active)
        {
            var effect = entry.Effect;
            render(effect);

            // The envelope walks AFTER the render: 1 (fade in) -> 0 (hold) -> 2 (fade out).
            effect.AdvancePhase(step.Countdown);
        }
    }

    /// <summary><c>CToleranceRenderStep::SelectEffects</c>.</summary>
    private void Select(RenderStep step)
    {
        var active = step.MutableActive;
        foreach (var entry in active) entry.Taken = false;
        active.Clear();

        var wanted = MpvisMath.MinOfK(_random, step.MinCount, step.MaxCount, step.CountDraws);
        step.Countdown = MpvisMath.RandLong(_random, step.MinLifetime, step.MaxLifetime);

        if (TrySelectPinned(step, wanted)) { RefreshDraws(); return; }

        for (var i = _pool.Length > 0 ? 99 : -1; wanted > 0 && i >= 0; i--)
        {
            var index = i < _pool.Length ? i : MpvisMath.RandLong(_random, 0, _pool.Length - 1);
            var entry = _pool[index];
            if (entry.Category != step.Category || entry.Taken) continue;
            if (!(_random.NextDouble() <= entry.Weight)) continue;

            Accept(step, entry);
            wanted--;
        }

        RefreshDraws();
    }

    private void Accept(RenderStep step, PoolEntry entry)
    {
        step.MutableActive.Add(entry);
        entry.Effect.Randomize(_random);

        var duration = step.Countdown / 3;
        if (MpvisMath.RandLong(_random, 10, 90) < duration)
            duration = MpvisMath.RandLong(_random, 10, 90);
        entry.Effect.BeginPhase(duration);
        entry.Taken = true;
    }

    /// <summary>Diagnostic pins. Returns true when the step was filled (or deliberately left empty) by a pin.</summary>
    private bool TrySelectPinned(RenderStep step, int wanted)
    {
        if (step.Category != CategoryRender || PinnedDraw is not { } draw) return false;

        var entry = (uint)draw < (uint)_drawEntries.Length ? _drawEntries[draw] : null;
        if (entry is not null && !entry.Taken && wanted > 0) Accept(step, entry);
        return true;
    }

    private void RefreshDraws()
    {
        _draws.Clear();
        foreach (var step in _steps)
            if (step.Category == CategoryRender)
                foreach (var entry in step.Active)
                    _draws.Add(entry.Effect);
    }

    /// <summary>Blur and SwitchBlur: pooled, but no step ever requests category 2.</summary>
    private sealed class InertEffect(string name) : AlchemyEffect
    {
        public override string Name => name;
    }
}
