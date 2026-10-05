using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Visualizations.Abstractions;
using PlattaPlayer.Visualizations.Wmp;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Visualizations.Wmp.BarsAndWaves;

namespace PlattaPlayer.Visualizations.Wmp.BarsAndWaves.Plugin;

/// <summary>
/// Hosts the bit-exact <see cref="BarsAndWavesEngine"/> on a plain Avalonia <see cref="Control"/>: no
/// GPU, a <see cref="WriteableBitmap"/> blitted from the engine's managed BGRA framebuffer.
///
/// Two details are deliberate rather than incidental:
/// <list type="bullet">
/// <item>The timer runs at <see cref="WmpFrameRate.WindowedIntervalMs"/>, not free-running. Every motion
/// constant in the effect is per-frame, so the tick rate IS the animation speed.</item>
/// <item>The surface is sized to full device pixels and presented 1:1. Unlike Alchemy and Battery this
/// effect has no fixed internal field — the real one rasterises directly at the destination rect — so
/// there is nothing to upscale.</item>
/// </list>
/// </summary>
public sealed class BarsAndWavesVisualizer : Control, IVisualizationController, IVisualizerHealth
{
    public static readonly StyledProperty<IAudioTap?> TapProperty =
        AvaloniaProperty.Register<BarsAndWavesVisualizer, IAudioTap?>(nameof(Tap));

    public IAudioTap? Tap
    {
        get => GetValue(TapProperty);
        set => SetValue(TapProperty, value);
    }

    /// <summary>Raised with the current preset's title, for the host's "now showing" label.</summary>
    public event Action<string>? NameChanged;

    private static readonly BarsAndWavesPreset[] Presets =
    [
        BarsAndWavesPreset.Bars,
        BarsAndWavesPreset.OceanMist,
        BarsAndWavesPreset.FireStorm,
        BarsAndWavesPreset.Scope,
    ];

    private readonly BarsAndWavesEngine _engine = new();
    private readonly TapToTimedLevel _adapter = new();

    private WriteableBitmap? _bitmap;
    private DispatcherTimer? _timer;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _presetIndex;
    private string _lastName = "";

    public long Heartbeat { get; private set; }

    public void Resume()
    {
        if (_timer is { IsEnabled: false }) _timer.Start();
    }

    public BarsAndWavesVisualizer()
    {
        // The engine starts at wmp.dll's constructor defaults and expects the host to choose a preset,
        // exactly as WMP does. Several config fields are sticky across preset changes, so this call is
        // what establishes a well-defined starting state.
        _engine.SetPreset(Presets[_presetIndex]);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer { Interval = WmpFrameRate.WindowedInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Heartbeat++;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        EnsureBitmap();
        if (_bitmap is null) return;

        var frame = _adapter.Update(Tap);
        _engine.Render(frame);

        var pixels = _engine.FrameBuffer;
        var needed = _bitmapWidth * _bitmapHeight;
        if (pixels.Length >= needed)
        {
            using var locked = _bitmap.Lock();
            Marshal.Copy(pixels, 0, locked.Address, needed);
        }

        RaiseNameIfChanged();
        InvalidateVisual();
    }

    private void EnsureBitmap()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));
        if (_bitmap is not null && width == _bitmapWidth && height == _bitmapHeight) return;

        _bitmapWidth = width;
        _bitmapHeight = height;
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _engine.Resize(width, height);
    }

    private void RaiseNameIfChanged()
    {
        var name = _engine.CurrentName;
        if (name == _lastName) return;
        _lastName = name;
        NameChanged?.Invoke(name);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is null) return;
        context.DrawImage(
            _bitmap,
            new Rect(0, 0, _bitmapWidth, _bitmapHeight),
            new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    public void NextPreset() => SelectPreset(_presetIndex + 1);

    public void PreviousPreset() => SelectPreset(_presetIndex - 1);

    /// <summary>
    /// WMP has no "random preset" concept for this effect; picking one at random is an accommodation to
    /// the host's controller contract, not a fidelity claim.
    /// </summary>
    public void RandomPreset() => SelectPreset(Random.Shared.Next(Presets.Length));

    private void SelectPreset(int index)
    {
        _presetIndex = ((index % Presets.Length) + Presets.Length) % Presets.Length;
        _engine.SetPreset(Presets[_presetIndex]);
    }
}
