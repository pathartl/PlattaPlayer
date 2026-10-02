using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Visualizations.Abstractions;
using PlattaPlayer.Visualizations.Wmp.Audio;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Plugin;

/// <summary>
/// The surface the Alchemy plugin ships with: the CPU <see cref="AlchemyCore"/> on a plain Avalonia
/// <see cref="Control"/>, presented through a <see cref="WriteableBitmap"/>.
///
/// This is the reference: the harness verb <c>verify-alchemy</c> renders the real mpvis.DLL and this
/// engine side by side from one rand() script and gets identical fields on every frame. It ships as
/// "Classic"; <see cref="WmpAlchemyVisualizer"/> is the same effect at window resolution. Speed is not a
/// concern: <c>time-alchemy</c> measures about 1.3 ms per frame on average against WMP's 16 ms.
///
/// As in the original:
/// <list type="bullet">
/// <item>The field is a FIXED 640x480, stretched to the control with no aspect correction and no
/// smoothing. mpvis presents it with <c>StretchBlt</c> in COLORONCOLOR mode, which replicates pixels.</item>
/// <item>The timer runs at <see cref="WmpFrameRate.WindowedIntervalMs"/>. Every motion constant is
/// per-frame, so the tick rate IS the animation speed.</item>
/// <item>The field only advances on fresh audio (TimedLevel state 2); otherwise it holds still.</item>
/// </list>
/// Effects are randomly scheduled, so next / previous / random all ask for a new warp kernel, the
/// nearest equivalent of the original's 'm' key.
/// </summary>
public sealed class WmpAlchemyCpuVisualizer : Control, IVisualizationController
{
    public static readonly StyledProperty<IAudioTap?> TapProperty =
        AvaloniaProperty.Register<WmpAlchemyCpuVisualizer, IAudioTap?>(nameof(Tap));

    public IAudioTap? Tap
    {
        get => GetValue(TapProperty);
        set => SetValue(TapProperty, value);
    }

    /// <summary>Private writable folder for the error log (host-supplied). When null, the log is skipped.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Raised whenever the active set of effects changes; carries its name.</summary>
    public event Action<string>? NameChanged;

    private const int FieldWidth = 640;
    private const int FieldHeight = 480;

    /// <summary>The rate the control is paced at. Passed to the engine as-is, never a measured rate.</summary>
    private const double NominalFps = 1000.0 / WmpFrameRate.WindowedIntervalMs;

    private readonly AlchemyCore _core = new(new Random(Environment.TickCount));
    private readonly TapToTimedLevel _adapter = new();
    private readonly TimedLevels _levels = new();
    private readonly WriteableBitmap _bitmap = new(
        new PixelSize(FieldWidth, FieldHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    private DispatcherTimer? _timer;
    private bool _pendingRandom;
    private bool _failed;
    private string _lastName = "";

    public WmpAlchemyCpuVisualizer()
    {
        _core.Resize(FieldWidth, FieldHeight);
        // Pixel replication, like StretchBlt in COLORONCOLOR mode, rather than a smoothed upscale.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
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
        if (_failed) return;
        try
        {
            _levels.LoadFrom(_adapter.Update(Tap));

            if (_pendingRandom)
            {
                _pendingRandom = false;
                _core.Reshuffle();
            }

            // Update gates itself on state 2, so a paused or stopped player leaves the field untouched.
            _core.Update(_levels, NominalFps);

            using (var locked = _bitmap.Lock())
                Marshal.Copy(_core.Pixels, 0, locked.Address, FieldWidth * FieldHeight);

            RaiseNameIfChanged();
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            // Match the other visualizers: log once and go dark rather than throw on every tick.
            _failed = true;
            LogFailure(ex);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawImage(_bitmap, new Rect(0, 0, FieldWidth, FieldHeight), new Rect(Bounds.Size));
    }

    public void NextPreset() => _pendingRandom = true;

    public void PreviousPreset() => _pendingRandom = true;

    public void RandomPreset() => _pendingRandom = true;

    private void RaiseNameIfChanged()
    {
        var name = _core.CurrentName;
        if (name == _lastName) return;
        _lastName = name;
        NameChanged?.Invoke(name);
    }

    private void LogFailure(Exception ex)
    {
        Debug.WriteLine($"WMP Alchemy failed: {ex}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            File.AppendAllText(Path.Combine(DataDirectory, "wmp-alchemy-error.log"),
                               $"[{DateTime.Now:O}] WMP Alchemy failed:\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }
}
