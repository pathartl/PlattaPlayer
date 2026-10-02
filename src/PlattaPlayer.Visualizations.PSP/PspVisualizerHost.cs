using PlattaPlayer.Visualizations.PSP.Common;
using PlattaPlayer.Visualizations.PSP.Gu;

namespace PlattaPlayer.Visualizations.PSP;

/// <summary>
/// Plays the part of the PSP's visualizer controller (paf widget "PhVisualizer", 0x11e4 / 0x1af4 /
/// 0x1e84): owns the active visualizer, builds the PCM block it is handed, writes the widget colour and
/// the cross-fade brightness into it each frame, and calls it. One frame = <see cref="RenderFrame"/>,
/// whose GU output is left in <see cref="Frame"/> for a renderer to replay.
/// </summary>
public sealed class PspVisualizerHost : IDisposable
{
    /// <summary>Stereo frames per PCM block (the controller hands over 0x1000 bytes).</summary>
    public const int BlockFrames = PcmBlock.BlockBytes / 4;

    /// <summary>The PSP's visualizers are paced by the XMB's 60 Hz refresh; all motion is per frame.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    private readonly IPspAssets _assets;
    private readonly Func<ReadOnlyMemory<byte>, PafImage?>? _decodeJpeg;
    private readonly short[] _samples = new short[BlockFrames * 2];
    private readonly PcmBlock _pcm;
    private PspRuntime _runtime;
    private Visualizer? _visualizer;
    private bool _shown;
    private int _fadeFrames;

    public PspVisualizerHost(IPspAssets assets, Func<ReadOnlyMemory<byte>, PafImage?>? decodeJpeg = null)
    {
        _assets = assets;
        _decodeJpeg = decodeJpeg;
        _pcm = new PcmBlock(_samples, PcmBlock.BlockBytes, 44100, 0);
        _runtime = new PspRuntime(assets, decodeJpeg);
    }

    /// <summary>The widget colour the PSP supplies (the theme colour), RGBA 0..1. Defaults to a mid blue.</summary>
    public float[] WidgetColor { get; } = [0.0f, 0.55f, 1.0f, 1.0f];

    /// <summary>The catalog entry that is showing, or null before <see cref="Select"/>.</summary>
    public PspVisualizerInfo? Current { get; private set; }

    /// <summary>The GU commands of the last <see cref="RenderFrame"/>.</summary>
    public GuFrame Frame => _runtime.Gu.Frame;

    /// <summary>
    /// Switches to <paramref name="info"/>. Each visualizer gets a fresh runtime, as each one ran in a
    /// freshly created object on the PSP (its globals aside), then fades in over its
    /// <see cref="Visualizer.FadeTimeMs"/>, as the controller cross-fades.
    /// </summary>
    public void Select(PspVisualizerInfo info)
    {
        _visualizer?.UnloadResources();
        _visualizer?.Dispose();
        _runtime = new PspRuntime(_assets, _decodeJpeg);
        Current = info;
        _visualizer = info.Create(_runtime);
        _visualizer.LoadResources();
        _shown = false;
        _fadeFrames = 0;
    }

    /// <summary>
    /// Runs one controller frame. <paramref name="left"/>/<paramref name="right"/> hold the latest
    /// <paramref name="frames"/> samples (-1..1); fewer than a block is zero-padded at the front, none at
    /// all means "no audio" (player state 1).
    /// </summary>
    public void RenderFrame(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int frames)
    {
        var gu = _runtime.Gu;
        gu.BeginFrame();
        if (_visualizer is not { } vis) return;

        FillBlock(left, right, frames);
        _runtime.PlayerState = frames > 0 ? 0 : 1;

        // 0x1af4: widget colour and opacity * cross-fade, clamped to [0, 1].
        for (var i = 0; i < 4; i++) vis.Color[i] = WidgetColor[i];
        vis.Alpha = Math.Clamp(WidgetColor[3], 0f, 1f);
        var fadeFrames = Math.Max(1f, vis.FadeTimeMs() / (float)FrameInterval.TotalMilliseconds);
        vis.Brightness = Math.Clamp(++_fadeFrames / fadeFrames, 0f, 1f);
        if (!_shown)
        {
            vis.OnShow();
            _shown = true;
        }

        vis.Update(_pcm);
        if (vis.IsLoaded()) vis.Render(_pcm);
        _pcm.Fresh = 0;
    }

    /// <summary>Forwards a player event (type 7 = track changed) to the visualizer.</summary>
    public void SendPlayerEvent(PlayerEvent ev)
    {
        if (_visualizer is null) return;
        // The controller makes sure resources exist before dispatching (several handlers rely on it).
        _visualizer.LoadResources();
        _visualizer.OnPlayerEvent(ev);
    }

    public void Dispose()
    {
        _visualizer?.UnloadResources();
        _visualizer?.Dispose();
        _visualizer = null;
    }

    private void FillBlock(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int frames)
    {
        frames = Math.Min(frames, Math.Min(left.Length, right.Length));
        var take = Math.Min(frames, BlockFrames);
        var pad = BlockFrames - take;
        Array.Clear(_samples, 0, pad * 2);
        for (var i = 0; i < take; i++)
        {
            var src = frames - take + i;
            _samples[(pad + i) * 2] = ToPcm16(left[src]);
            _samples[(pad + i) * 2 + 1] = ToPcm16(right[src]);
        }
        _pcm.Bytes = PcmBlock.BlockBytes;
        _pcm.Fresh = 1;
    }

    private static short ToPcm16(float s) => (short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue);
}
