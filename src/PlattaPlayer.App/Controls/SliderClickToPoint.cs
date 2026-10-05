using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PlattaPlayer.App.Controls;

/// <summary>
/// Makes a press anywhere on a <see cref="Slider"/> jump the value to that point and keep following the pointer
/// until release. Avalonia's own click-to-point is wired to the Track's repeat buttons, which the custom
/// Seek/Volume template leaves at zero width, so presses off the thumb would otherwise do nothing. Presses on
/// the thumb are left alone for its normal drag.
/// </summary>
public static class SliderClickToPoint
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Slider, bool>("IsEnabled", typeof(SliderClickToPoint));

    private static readonly AttachedProperty<bool> IsDraggingProperty =
        AvaloniaProperty.RegisterAttached<Slider, bool>("IsDragging", typeof(SliderClickToPoint));

    public static bool GetIsEnabled(Slider slider) => slider.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Slider slider, bool value) => slider.SetValue(IsEnabledProperty, value);

    static SliderClickToPoint()
    {
        IsEnabledProperty.Changed.AddClassHandler<Slider>((slider, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                slider.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
                slider.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
                slider.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
                slider.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            }
            else
            {
                slider.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
                slider.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
                slider.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
                slider.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
            }
        });
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source && source.FindAncestorOfType<Thumb>(includeSelf: true) is not null) return;

        MoveToPoint(slider, e);
        slider.SetValue(IsDraggingProperty, true);
        e.Pointer.Capture(slider);
        e.Handled = true;
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Slider slider && slider.GetValue(IsDraggingProperty))
            MoveToPoint(slider, e);
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Slider slider || !slider.GetValue(IsDraggingProperty)) return;
        slider.SetValue(IsDraggingProperty, false);
        e.Pointer.Capture(null);
    }

    private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Slider slider)
            slider.SetValue(IsDraggingProperty, false);
    }

    // Mirrors Slider.MoveToPoint: the usable span is the track less the thumb, so the thumb centres on the pointer.
    private static void MoveToPoint(Slider slider, PointerEventArgs e)
    {
        var track = slider.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        if (track is null || track.Bounds.Width <= 0) return;

        var thumb = track.Thumb?.Bounds.Width ?? 0;
        var span = Math.Max(track.Bounds.Width - thumb, 1);
        var fraction = Math.Clamp((e.GetPosition(track).X - thumb / 2) / span, 0, 1);
        if (slider.IsDirectionReversed) fraction = 1 - fraction;

        slider.SetCurrentValue(RangeBase.ValueProperty, slider.Minimum + fraction * (slider.Maximum - slider.Minimum));
    }
}
