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

namespace PlattaPlayer.Visualizations.Wmp.Battery.Plugin;

/// <summary>
/// The Classic Battery surface: the CPU <see cref="BatteryCore"/> on a plain Avalonia
/// <see cref="Control"/>, presented through a <see cref="WriteableBitmap"/>.
///
/// It is the reference. The harness verb <c>verify-battery</c> runs wmp.dll's Battery and this engine
/// from one rand() script and gets identical fields, palettes and presented pixels on every frame.
///
/// As in the original:
/// <list type="bullet">
/// <item>The field is a FIXED 384x288. It is stretched to the control with no aspect correction and no
/// smoothing, the way <c>StretchBlt</c> in COLORONCOLOR mode replicates pixels.</item>
/// <item>The timer runs at <see cref="WmpFrameRate.WindowedIntervalMs"/>. Every motion constant is
/// per-frame, so the tick rate IS the animation speed.</item>
/// <item>Playing renders, paused holds, and stopped fades to palette index 1 over 300 frames and then
/// fills with that colour.</item>
/// </list>
/// Next / previous / random step through the 26 presets. Preset 0 is "Randomization".
/// </summary>
public sealed class WmpBatteryCpuVisualizer : Control, IVisualizationController
{
    public static readonly StyledProperty<IAudioTap?> TapProperty =
        AvaloniaProperty.Register<WmpBatteryCpuVisualizer, IAudioTap?>(nameof(Tap));

    public IAudioTap? Tap
    {
        get => GetValue(TapProperty);
        set => SetValue(TapProperty, value);
    }

    /// <summary>Private writable folder for the error log (host-supplied). When null, the log is skipped.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Raised when the preset changes; carries its title.</summary>
    public event Action<string>? NameChanged;

    private const int FieldWidth = BatteryCore.FieldWidth;
    private const int FieldHeight = BatteryCore.FieldHeight;

    // Battery's creator does srand((unsigned)_time64(NULL)).
    private readonly BatteryCore _core = new(seed: (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private readonly TapToTimedLevel _adapter = new();
    private readonly int[] _pixels = new int[FieldWidth * FieldHeight];
    private readonly WriteableBitmap _bitmap = new(
        new PixelSize(FieldWidth, FieldHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    private readonly Random _random = new();

    private DispatcherTimer? _timer;
    private int _requestedPreset = -1;
    private bool _failed;
    private string _lastName = "";

    public WmpBatteryCpuVisualizer()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer { Interval = WmpFrameRate.WindowedInterval };
        _timer.Tick += OnTick;
        _timer.Start();
        RaiseNameIfChanged();
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
            if (_requestedPreset >= 0)
            {
                _core.SetCurrentPreset(_requestedPreset);
                _requestedPreset = -1;
                RaiseNameIfChanged();
            }

            if (_core.Render(_adapter.Update(Tap)))
            {
                _core.CopyTo(_pixels);
            }
            else
            {
                var fill = BatteryCore.ToArgb(_core.StopFillColor);
                Array.Fill(_pixels, fill);
            }

            using (var locked = _bitmap.Lock())
                Marshal.Copy(_pixels, 0, locked.Address, _pixels.Length);
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

    public void NextPreset() => _requestedPreset = (Pending + 1) % _core.PresetCount;

    public void PreviousPreset() => _requestedPreset = (Pending - 1 + _core.PresetCount) % _core.PresetCount;

    public void RandomPreset() => _requestedPreset = _random.Next(_core.PresetCount);

    private int Pending => _requestedPreset >= 0 ? _requestedPreset : _core.CurrentPreset;

    private void RaiseNameIfChanged()
    {
        var name = _core.PresetTitle(_core.CurrentPreset);
        if (name == _lastName) return;
        _lastName = name;
        NameChanged?.Invoke(name);
    }

    private void LogFailure(Exception ex)
    {
        Debug.WriteLine($"WMP Battery failed: {ex}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            File.AppendAllText(Path.Combine(DataDirectory, "wmp-battery-error.log"),
                               $"[{DateTime.Now:O}] WMP Battery failed:\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }
}
