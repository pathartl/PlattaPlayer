using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PlattaPlayer.App.Controls;

/// <summary>
/// The "now playing" mark: three 3px accent bars bouncing between 4px and 14px (0.9s ease-in-out, staggered
/// 0 / 0.3 / 0.15s). Shown on the playing artist card, album tile and track row. When
/// <see cref="IsActive"/> is false (paused) the bars freeze at rest height.
/// </summary>
public sealed class NowPlayingIndicator : Control
{
    private const double BarWidth = 3, Gap = 2, RestMin = 4, RestMax = 14, Period = 0.9;
    private static readonly double[] Delays = { 0, 0.3, 0.15 };

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<NowPlayingIndicator, bool>(nameof(IsActive));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<NowPlayingIndicator, IBrush?>(nameof(BarBrush));

    private readonly DateTime _start = DateTime.UtcNow;
    private bool _frameRequested;

    static NowPlayingIndicator() => AffectsRender<NowPlayingIndicator>(IsActiveProperty, BarBrushProperty);

    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(BarWidth * 3 + Gap * 2, RestMax);

    public override void Render(DrawingContext context)
    {
        var brush = BarBrush ?? Brushes.White;
        var t = (DateTime.UtcNow - _start).TotalSeconds;

        for (var i = 0; i < 3; i++)
        {
            double height;
            if (IsActive)
            {
                // Cosine ease between min and max, matching CSS ease-in-out keyframes 0%/50%/100%.
                var phase = ((t - Delays[i]) % Period + Period) % Period / Period;
                height = RestMin + (RestMax - RestMin) * (0.5 - 0.5 * Math.Cos(phase * 2 * Math.PI));
            }
            else
            {
                height = new[] { 8d, 12d, 6d }[i];
            }

            var x = i * (BarWidth + Gap);
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, RestMax - height, BarWidth, height), 1));
        }

        if (IsActive)
            RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            if (IsActive && IsEffectivelyVisible) InvalidateVisual();
        });
    }
}
