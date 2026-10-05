using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace PlattaPlayer.App.Controls;

/// <summary>
/// Shows its child at full width, clipped to the available width. When the child doesn't fit it sits still
/// for <see cref="HoldDuration"/>, eases across until the end is visible, holds again, eases back, and
/// repeats. The edges that hide content fade out. The cycle restarts whenever the content or the available
/// width changes (a new track) and whenever the marquee comes back into view.
/// </summary>
public sealed class Marquee : Decorator
{
    private const double FadeWidth = 24;

    public static readonly StyledProperty<TimeSpan> HoldDurationProperty =
        AvaloniaProperty.Register<Marquee, TimeSpan>(nameof(HoldDuration), TimeSpan.FromSeconds(3));

    /// <summary>Scroll speed in pixels per second (average, the move is eased).</summary>
    public static readonly StyledProperty<double> SpeedProperty =
        AvaloniaProperty.Register<Marquee, double>(nameof(Speed), 40);

    private readonly LinearGradientBrush _mask = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0),
            new GradientStop(Colors.Black, 1), new GradientStop(Colors.Black, 1),
        },
    };

    private DateTime _start = DateTime.UtcNow;
    private double _contentWidth, _viewWidth, _offset;
    private bool _frameRequested, _visibilityPollPending;

    public Marquee() => ClipToBounds = true;

    public TimeSpan HoldDuration { get => GetValue(HoldDurationProperty); set => SetValue(HoldDurationProperty, value); }
    public double Speed { get => GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }

    private double Overflow => Math.Max(0, _contentWidth - _viewWidth);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null) return default;
        Child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var desired = Child.DesiredSize;
        var width = desired.Width;
        if (Child is TextBlock text) width += Math.Max(0, -text.TextLayout.OverhangTrailing);
        return new Size(Math.Min(width, availableSize.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null) return finalSize;
        var contentWidth = Child.DesiredSize.Width;
        // A TextBlock's desired width is the advance width; the last glyph's ink can stick out past it
        // (negative trailing overhang), which would otherwise stay clipped at the end of the scroll.
        if (Child is TextBlock text) contentWidth += Math.Max(0, -text.TextLayout.OverhangTrailing);
        if (contentWidth != _contentWidth || finalSize.Width != _viewWidth)
        {
            _contentWidth = contentWidth;
            _viewWidth = finalSize.Width;
            Restart();
        }
        Child.Arrange(new Rect(-_offset, 0, Math.Max(contentWidth, finalSize.Width), finalSize.Height));
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Restart();
    }

    private void Restart()
    {
        _start = DateTime.UtcNow;
        SetOffset(0);
        if (Overflow > 0) RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            Tick();
        });
    }

    private void Tick()
    {
        if (Overflow <= 0 || TopLevel.GetTopLevel(this) is null) { SetOffset(0); return; }

        // Hidden (e.g. Now Playing closed): park at the start and check back now and then rather than
        // burning frames, so the cycle begins with a hold when it's shown again.
        if (!IsEffectivelyVisible)
        {
            _start = DateTime.UtcNow;
            SetOffset(0);
            if (!_visibilityPollPending)
            {
                _visibilityPollPending = true;
                DispatcherTimer.RunOnce(() => { _visibilityPollPending = false; Tick(); }, TimeSpan.FromMilliseconds(500));
            }
            return;
        }

        var hold = HoldDuration.TotalSeconds;
        var move = Overflow / Math.Max(1, Speed);
        var t = (DateTime.UtcNow - _start).TotalSeconds % (2 * (hold + move));

        double position; // 0 = start, 1 = end
        if (t < hold) position = 0;
        else if (t < hold + move) position = Ease((t - hold) / move);
        else if (t < 2 * hold + move) position = 1;
        else position = 1 - Ease((t - 2 * hold - move) / move);

        SetOffset(position * Overflow);
        RequestFrame();
    }

    private static double Ease(double x) => 0.5 - 0.5 * Math.Cos(x * Math.PI);

    private void SetOffset(double offset)
    {
        var overflow = Overflow;
        if (overflow > 0 && _viewWidth > 0)
        {
            // Fade an edge in proportion to how much content is hidden behind it, up to FadeWidth.
            var f = Math.Min(0.5, FadeWidth / _viewWidth);
            var stops = _mask.GradientStops;
            stops[0].Color = Color.FromArgb((byte)(255 * (1 - Math.Clamp(offset / FadeWidth, 0, 1))), 0, 0, 0);
            stops[1].Offset = f;
            stops[2].Offset = 1 - f;
            stops[3].Color = Color.FromArgb((byte)(255 * (1 - Math.Clamp((overflow - offset) / FadeWidth, 0, 1))), 0, 0, 0);
            OpacityMask = _mask;
        }
        else
        {
            OpacityMask = null;
        }

        if (offset == _offset) return;
        _offset = offset;
        InvalidateArrange();
    }
}
