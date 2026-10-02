using System;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy;

/// <summary>
/// The "Alchemy" feedback-warp engine top level (analysis §B.2 main loop). Each frame it refreshes the
/// <see cref="AudioSnapshot"/>, lets the <see cref="EffectScheduler"/> advance / swap effects, ticks the
/// active effects, rebuilds the per-pixel displacement map from the active warp(s), gathers the previous
/// frame through it with a diffusion+decay (the motion trails), then draws the overlay geometry on top.
/// The result lives in <see cref="Pixels"/> (BGRA), ready for the host to blit.
/// </summary>
public sealed class AlchemyCore
{
    private const int EdgeColor = unchecked((int)0xFF000000);

    private readonly PixelBuffer _frame = new();
    private int[] _scratch = Array.Empty<int>();
    private readonly WarpMap _warpMap = new();
    private readonly AudioSnapshot _snapshot = new();
    private readonly EffectScheduler _scheduler;
    private readonly EffectContext _ctx = new();
    private readonly DrawPrimitives _draw = new();
    private readonly Random _random;
    private readonly Action<AlchemyEffect> _render;
    private int[] _morphFrom = Array.Empty<int>();
    private int[] _morphMap = Array.Empty<int>();
    private int _promotionsSeen;
    private int _frameCounter;

    public AlchemyCore(Random random)
    {
        _random = random;
        _scheduler = new EffectScheduler(new EffectRegistry(), random);
        _render = RenderEffect;
    }

    /// <summary>Current effect label for the host's "preset name".</summary>
    public string CurrentName => _scheduler.CurrentName;

    /// <summary>The scheduler, for diagnostics (the harness compares it with the real one).</summary>
    public EffectScheduler Scheduler => _scheduler;

    /// <summary>The live BGRA frame.</summary>
    public int[] Pixels => _frame.Pixels;

    public int Width => _frame.Width;
    public int Height => _frame.Height;

    /// <summary>
    /// Diagnostic: the map the last frame's feedback pass gathered through (the morph's blend while one
    /// runs), or null if that frame had no warp. The harness replays it to check the GPU path.
    /// </summary>
    public int[]? LastMap { get; private set; }

    public void Resize(int w, int h)
    {
        _frame.Resize(w, h);
        var count = _frame.Width * _frame.Height;
        if (_scratch.Length != count) _scratch = new int[count];
        _warpMap.Resize(_frame.Width, _frame.Height);
        _scheduler.Resize(_frame.Width, _frame.Height);
    }

    /// <summary>TimedLevel.state value meaning "playing, fresh data" — the only state that advances.</summary>
    private const int FreshAudioState = 2;

    /// <summary>Force a fresh set of effects (host RandomPreset).</summary>
    public void Reshuffle() => _scheduler.Reshuffle();

    /// <summary>
    /// Diagnostic: hold one specific warp kernel and/or renderer instead of scheduling at random, so a
    /// single kernel's effect on the feedback field can be measured on its own. Used by the
    /// reverse-engineering harness; null restores normal scheduling.
    /// </summary>
    public void Pin(int? warp, int? draw) => _scheduler.Pin(warp, draw);

    public void Update(TimedLevels levels, double fps)
    {
        var w = _frame.Width;
        var h = _frame.Height;
        if (w < 2 || h < 2) return;

        // The real effect advances ONLY on fresh audio. mpvis's Render (FUN_180008170) gates the entire
        // per-frame update — warp, feedback and overlay alike — behind `state == 2` and otherwise just
        // re-presents the existing field. Advancing unconditionally, as this used to, meant the
        // visualization kept churning while paused or stopped instead of holding still, and it consumed
        // effect lifetimes against silence.
        if (levels.State != FreshAudioState) return;

        LastMap = null;
        _snapshot.Update(levels, _random);

        _ctx.Width = w;
        _ctx.Height = h;
        _ctx.Audio = _snapshot;
        _ctx.Random = _random;
        _ctx.Frame = _frameCounter++;
        _ctx.Fps = fps;
        _ctx.Draw = _draw;
        // Every spline POINT steps the stroke's colour transition forward, so it needs the engine
        // RNG for the point where a transition runs out and a fresh target is drawn.
        _draw.Random = _random;
        _draw.Palette ??= new PaletteCycler();
        // No stroke thickening. This used to scale the dot with the surface, which was a workaround for
        // hairline strokes in a large window — but the field is a fixed 640x480 that the host replicates
        // up, exactly as the original does, so there is nothing to compensate for and the original's
        // footprint is a fixed 3x3 regardless of size.

        _scheduler.Run(_render);
    }

    /// <summary>
    /// Called by the scheduler for each held effect, in step order, where the original calls NormalRender.
    /// The warp stage (step 0) builds the map and runs the feedback pass, the renderers (steps 1 and 5)
    /// tick and draw on top, and the bass bounce (step 7) advances its zoom — which the map therefore
    /// reads on the FOLLOWING frame.
    /// </summary>
    private void RenderEffect(AlchemyEffect effect)
    {
        if (effect is WarpStage stage)
        {
            stage.Tick(_ctx);
            var warpA = _scheduler.WarpA;
            if (warpA is null) return;
            warpA.Tick(_ctx);
            stage.CurrentB?.Tick(_ctx);

            // A promotion swaps in the pending kernels. Last frame's map is still the OLD kernels' table
            // (promotions never happen mid-morph), so freeze it now, before building the new one: the
            // released kernels can be re-randomized at once and could not rebuild it later.
            if (stage.Promotions != _promotionsSeen)
            {
                _promotionsSeen = stage.Promotions;
                if (_morphFrom.Length != _warpMap.Map.Length) _morphFrom = new int[_warpMap.Map.Length];
                Array.Copy(_warpMap.Map, _morphFrom, _morphFrom.Length);
            }

            _warpMap.Build(_ctx, warpA, _scheduler.WarpB);
            var map = _warpMap.Map;
            if (stage.Transitioning)
            {
                if (_morphMap.Length != map.Length) _morphMap = new int[map.Length];
                WarpMap.Morph(_morphFrom, map, _morphMap, _frame.Width, stage.MorphIndex);
                map = _morphMap;
            }

            LastMap = map;
            FeedbackPass.GatherAndDecay(_frame.Pixels, _scratch, map, _frame.Width, _frame.Height, EdgeColor);
            return;
        }

        effect.Tick(_ctx);
        if (effect.IsDraw) effect.Draw(_frame, _ctx);
    }
}
