using System;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Visualizations.Wmp.Framebuffer;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// The GPU analogue of <see cref="AlchemyCore"/>: the same frame, minus the per-pixel work.
///
/// It runs the identical per-frame machinery, with the same audio gate, scheduler, ticks, and the
/// renderers' own <see cref="AlchemyEffect.Draw"/>. Then, instead of building a warp map and running
/// the feedback pass, it harvests the kernels' parameters for the shader. Instead of rasterizing, the
/// renderers draw into an <see cref="IStrokeRecorder"/>. Nothing upstream of the pixels differs, so for
/// one seed and one audio stream this engine and <see cref="AlchemyCore"/> make the same choices and the
/// same random draws on every frame (the harness verb <c>verify-gpu-engine</c> checks this).
/// </summary>
public sealed class AlchemyGpuEngine
{
    /// <summary>TimedLevel.state value meaning "playing, fresh data", the only state that advances.</summary>
    private const int FreshAudioState = 2;

    private readonly AudioSnapshot _snapshot = new();
    private readonly EffectScheduler _scheduler;
    private readonly EffectContext _ctx = new();
    private readonly DrawPrimitives _draw = new();
    private readonly Random _random;
    private readonly Action<AlchemyEffect> _render;

    /// <summary>
    /// The renderers draw into this only for its size: with a recorder set, nothing reads or writes its
    /// pixels.
    /// </summary>
    private readonly PixelBuffer _bounds = new();

    private AlchemyGpuFrame? _frame;
    private int _frameCounter;

    public AlchemyGpuEngine(Random random)
    {
        _random = random;
        _scheduler = new EffectScheduler(new EffectRegistry(), random);
        _render = RenderEffect;
    }

    /// <summary>Current effect label for the host's "preset name".</summary>
    public string CurrentName => _scheduler.CurrentName;

    /// <summary>The scheduler, for diagnostics.</summary>
    public EffectScheduler Scheduler => _scheduler;

    public int Width => _bounds.Width;
    public int Height => _bounds.Height;

    /// <summary>Set the FIELD size (not the device size; see <see cref="AlchemyFieldScale"/>).</summary>
    public void Resize(int w, int h)
    {
        _bounds.Resize(w, h);
        _scheduler.Resize(_bounds.Width, _bounds.Height);
    }

    /// <summary>Force a fresh set of effects (host RandomPreset).</summary>
    public void Reshuffle() => _scheduler.Reshuffle();

    /// <summary>Diagnostic pinning, as <see cref="AlchemyCore.Pin"/>.</summary>
    public void Pin(int? warp, int? draw) => _scheduler.Pin(warp, draw);

    /// <summary>
    /// Advance one frame into <paramref name="frame"/>. Returns false, leaving the frame untouched,
    /// when the audio is not fresh: the original only advances on fresh audio and otherwise
    /// re-presents the field.
    /// </summary>
    public bool Update(TimedLevels levels, double fps, AlchemyGpuFrame frame)
    {
        var w = _bounds.Width;
        var h = _bounds.Height;
        if (w < 2 || h < 2) return false;
        if (levels.State != FreshAudioState) return false;

        _snapshot.Update(levels, _random);

        _ctx.Width = w;
        _ctx.Height = h;
        _ctx.Audio = _snapshot;
        _ctx.Random = _random;
        _ctx.Frame = _frameCounter++;
        _ctx.Fps = fps;
        _ctx.Draw = _draw;
        _draw.Random = _random;
        _draw.Palette ??= new PaletteCycler();
        _draw.Recorder = frame.Strokes;

        frame.FieldWidth = w;
        frame.FieldHeight = h;
        frame.HasWarp = false;
        frame.Strokes.Clear();

        _frame = frame;
        _scheduler.Run(_render);
        _frame = null;

        frame.Strokes.Finish();
        frame.CurrentName = _scheduler.CurrentName;
        return true;
    }

    /// <summary>
    /// <see cref="AlchemyCore"/>'s per-effect render with the pixels taken out: the warp stage ticks its
    /// kernels and hands over their parameters, and the renderers tick and draw into the recorder.
    /// </summary>
    private void RenderEffect(AlchemyEffect effect)
    {
        var frame = _frame!;
        if (effect is WarpStage stage)
        {
            stage.Tick(_ctx);
            var warpA = _scheduler.WarpA;
            if (warpA is null) return;
            warpA.Tick(_ctx);
            stage.CurrentB?.Tick(_ctx);

            frame.HasWarp = true;
            frame.Promotions = stage.Promotions;
            frame.Morphing = stage.Transitioning;
            frame.MorphIndex = stage.MorphIndex;
            Harvest(warpA, frame.Warp.A);
            frame.Warp.HasB = _scheduler.WarpB is { IsWarp: true };
            if (frame.Warp.HasB) Harvest(_scheduler.WarpB!, frame.Warp.B);
            else frame.Warp.B.Clear();
            return;
        }

        effect.Tick(_ctx);
        if (effect.IsDraw) effect.Draw(_bounds, _ctx);
    }

    private void Harvest(AlchemyEffect effect, GpuWarpSlot slot)
    {
        if (effect is not IGpuWarp warp || !effect.IsWarp)
        {
            slot.Clear();
            return;
        }

        slot.Source = effect;
        slot.Kind = warp.WarpKind;
        slot.ToOrigin = effect.OutOfRangeToOrigin;
        Array.Clear(slot.Params);
        warp.WriteParams(slot.Params, _ctx);
        slot.ChildKind = warp.ChildKind;
        Array.Clear(slot.ChildParams);
        warp.WriteChildParams(slot.ChildParams, _ctx);
    }
}
