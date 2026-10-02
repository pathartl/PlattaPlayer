using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PlattaPlayer.App.Controls;

/// <summary>
/// The track's loudness envelope drawn as a soft, mirrored silhouette behind the seek bar. It takes no layout
/// space: the shape is <see cref="WaveHeight"/> tall, centred on the control, and may spill past its bounds so
/// the seek row keeps its height. Not hit-testable, so clicks reach the slider underneath.
/// </summary>
public sealed class WaveformStrip : Control
{
    // One outline point every few pixels is plenty for an already-smoothed envelope.
    private const double PointSpacing = 2;

    public static readonly StyledProperty<float[]?> WaveformProperty =
        AvaloniaProperty.Register<WaveformStrip, float[]?>(nameof(Waveform));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<WaveformStrip, IBrush?>(nameof(Fill), new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));

    public static readonly StyledProperty<double> WaveHeightProperty =
        AvaloniaProperty.Register<WaveformStrip, double>(nameof(WaveHeight), 24);

    static WaveformStrip()
    {
        AffectsRender<WaveformStrip>(WaveformProperty, FillProperty, WaveHeightProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<WaveformStrip>(false);
    }

    public float[]? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double WaveHeight { get => GetValue(WaveHeightProperty); set => SetValue(WaveHeightProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => default;

    public override void Render(DrawingContext context)
    {
        if (Waveform is not { Length: > 1 } wave || Fill is not { } fill) return;
        var width = Bounds.Width;
        if (width <= 0) return;

        var mid = Bounds.Height / 2;
        var half = WaveHeight / 2;
        var points = Math.Max(2, (int)(width / PointSpacing) + 1);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // Top edge left → right, then the mirrored bottom edge back.
            for (var i = 0; i < points; i++)
            {
                var x = width * i / (points - 1);
                var y = mid - half * Sample(wave, (double)i / (points - 1));
                if (i == 0) ctx.BeginFigure(new Point(x, y), isFilled: true);
                else ctx.LineTo(new Point(x, y));
            }
            for (var i = points - 1; i >= 0; i--)
            {
                var x = width * i / (points - 1);
                ctx.LineTo(new Point(x, mid + half * Sample(wave, (double)i / (points - 1))));
            }
            ctx.EndFigure(isClosed: true);
        }

        context.DrawGeometry(fill, null, geometry);
    }

    /// <summary>Linearly interpolates the envelope at <paramref name="t"/> in [0, 1].</summary>
    private static double Sample(float[] wave, double t)
    {
        var pos = t * (wave.Length - 1);
        var i = Math.Min((int)pos, wave.Length - 2);
        var frac = pos - i;
        return wave[i] + (wave[i + 1] - wave[i]) * frac;
    }
}
