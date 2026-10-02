using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.Controls;

/// <summary>
/// Renders a log-frequency spectrum from an <see cref="IAudioTap"/>. Polls the tap on a render
/// timer (~60 fps), aggregates the raw FFT bins into a handful of bars on a logarithmic frequency
/// scale, and applies an asymmetric decay (instant rise, gentle fall) so the bars feel musical.
/// </summary>
public sealed class SpectrumVisualizer : Control
{
    // The tap exposes 1024 bins from a 2048-point FFT over the full 0..nyquist range.
    private const int FftBins = 1024;
    private const double Nyquist = 22050.0;   // 44.1 kHz / 2
    private const double MinFreq = 40.0;
    private const double MaxFreq = 16000.0;
    private const double FallRate = 0.82;     // higher = slower fall

    private readonly float[] _fft = new float[FftBins];
    private double[] _levels = Array.Empty<double>();
    private DispatcherTimer? _timer;

    public static readonly StyledProperty<IAudioTap?> TapProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, IAudioTap?>(nameof(Tap));

    public static readonly StyledProperty<int> BarCountProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, int>(nameof(BarCount), 64);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, double>(nameof(Gap), 2.0);

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<SpectrumVisualizer, IBrush?>(
            nameof(BarBrush), new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));

    public IAudioTap? Tap
    {
        get => GetValue(TapProperty);
        set => SetValue(TapProperty, value);
    }

    public int BarCount
    {
        get => GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var bars = Math.Max(1, BarCount);
        if (_levels.Length != bars) _levels = new double[bars];

        var tap = Tap;
        var active = tap is { IsActive: true } && tap.ReadSpectrum(_fft) > 0;

        var anyVisible = false;
        for (var b = 0; b < bars; b++)
        {
            var target = active ? BandLevel(b, bars) : 0.0;
            // Instant attack, smooth release.
            _levels[b] = target > _levels[b] ? target : _levels[b] * FallRate;
            if (_levels[b] > 0.001) anyVisible = true;
        }

        // Keep redrawing while there is motion; idle quietly once the bars have settled to zero.
        if (anyVisible || active) InvalidateVisual();
    }

    /// <summary>Peak magnitude in this bar's log-frequency slice, mapped to a 0..1 perceptual level.</summary>
    private double BandLevel(int bar, int bars)
    {
        var ratio = MaxFreq / MinFreq;
        var f0 = MinFreq * Math.Pow(ratio, (double)bar / bars);
        var f1 = MinFreq * Math.Pow(ratio, (double)(bar + 1) / bars);

        var lo = Math.Clamp((int)(f0 / Nyquist * FftBins), 0, FftBins - 1);
        var hi = Math.Clamp((int)(f1 / Nyquist * FftBins), lo + 1, FftBins);

        var peak = 0f;
        for (var i = lo; i < hi; i++)
            if (_fft[i] > peak) peak = _fft[i];

        // Magnitudes are tiny; a dB mapping spreads them across the visible range.
        var db = 20.0 * Math.Log10(peak + 1e-9);
        return Math.Clamp((db + 55.0) / 55.0, 0.0, 1.0);
    }

    public override void Render(DrawingContext context)
    {
        var brush = BarBrush;
        if (brush is null || _levels.Length == 0) return;

        var bars = _levels.Length;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var gap = Gap;
        var barW = (w - (bars - 1) * gap) / bars;
        if (barW <= 0) return;

        var radius = Math.Min(barW / 2.0, 3.0);

        for (var b = 0; b < bars; b++)
        {
            var barH = _levels[b] * h;
            if (barH < 1) continue;

            var x = b * (barW + gap);
            var rect = new Rect(x, h - barH, barW, barH);
            context.DrawRectangle(brush, null, rect, radius, radius);
        }
    }
}
