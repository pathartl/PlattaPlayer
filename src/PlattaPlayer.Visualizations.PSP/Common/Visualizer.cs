using PlattaPlayer.Visualizations.PSP.Gu;

namespace PlattaPlayer.Visualizations.PSP.Common;

/// <summary>An event the controller forwards to the active visualizer (0x1f98).</summary>
/// <param name="Type">Event type. 7 is taken to mean "track changed" (it is not one of the player states).</param>
/// <param name="Arg">Event argument.</param>
public readonly record struct PlayerEvent(int Type, int Arg);

/// <summary>
/// Base class shared by all seven visualizers (ctor 0x10cc, vtable 0x145b8), src/common/visualizer.h.
///
/// The controller (paf widget "PhVisualizer", ctor 0x11e4) owns one Visualizer at a time, created by
/// Vis_CreateVisualizer (0x17f8) from the user's selection (type 1..7 -> thumbnail
/// music_tex_vis_thum_(type-1)). Per frame the controller copies the widget's colour/opacity into the
/// fields below (0x1af4) and then calls <see cref="Render"/> with the latest PCM block (0x1e84).
///
/// Everything the original reached through globals or paf (GU, rand(), the player state, RCO textures)
/// comes from <see cref="Runtime"/>.
/// </summary>
public abstract class Visualizer : IDisposable
{
    protected Visualizer(PspRuntime runtime)
    {
        Runtime = runtime;
        // 0x10cc: colour components start as a NaN pattern (0x7f800001) until the controller writes
        // the widget colour on the first frame.
        for (var i = 0; i < Color.Length; i++) Color[i] = VisMath.BitsFloat(0x7f800001u);
    }

    /// <summary>The emulated PSP services this visualizer draws and reads through.</summary>
    public PspRuntime Runtime { get; }

    /// <summary>Shorthand for <c>Runtime.Gu</c>.</summary>
    protected GuContext Gu => Runtime.Gu;

    /// <summary>+0x04: widget colour alpha, clamped to [0,1].</summary>
    public float Alpha { get; set; }

    /// <summary>+0x10: widget colour RGBA (0..1).</summary>
    public float[] Color { get; } = new float[4];

    /// <summary>+0x24: widget opacity * cross-fade, clamped to [0,1].</summary>
    public float Brightness { get; set; }

    /// <summary>+0x08: per-frame, from the widget draw (0x1af4). Only type 1 overrides it.</summary>
    public virtual void Update(PcmBlock pcm) { }

    /// <summary>+0x0c: main entry: analyse PCM and issue GU drawing.</summary>
    public virtual void Render(PcmBlock pcm) { }

    /// <summary>+0x10: first frame after becoming visible.</summary>
    public virtual void OnShow() { }

    /// <summary>+0x14</summary>
    public virtual void OnHide() { }

    /// <summary>+0x18</summary>
    public virtual void OnPlayerEvent(PlayerEvent ev) { }

    /// <summary>+0x1c: create textures/surfaces.</summary>
    public virtual void LoadResources() { }

    /// <summary>+0x20</summary>
    public virtual void UnloadResources() { }

    /// <summary>+0x24: <see cref="Render"/> is only called when true.</summary>
    public virtual bool IsLoaded() => true;

    /// <summary>+0x2c (0x10408): cross-fade duration between visualizers.</summary>
    public virtual float FadeTimeMs() => 200.0f;

    /// <summary>+0x34 (0x1042c): passed to paf after each Render (0x1e84).</summary>
    public virtual int RenderInterval() => 10;

    /// <summary>The original's destructor.</summary>
    public virtual void Dispose() => GC.SuppressFinalize(this);
}
