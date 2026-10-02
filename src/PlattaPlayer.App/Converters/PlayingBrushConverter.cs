using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PlattaPlayer.App.Converters;

/// <summary>
/// [itemId, playingId] → the accent brush when they match (the playing album tile / track row title),
/// otherwise <see cref="Normal"/>.
/// </summary>
public sealed class PlayingBrushConverter : IMultiValueConverter
{
    // The shared resource brush, so a changed accent color (Settings) shows up here too.
    private static IBrush Accent =>
        Application.Current?.FindResource("AccentBrush") as IBrush ?? Brushes.White;

    /// <summary>Accent when playing, else white (album tile titles).</summary>
    public static readonly PlayingBrushConverter Strong = new() { Normal = Brushes.White };

    /// <summary>Accent when playing, else primary text (track rows).</summary>
    public static readonly PlayingBrushConverter Primary = new() { Normal = new SolidColorBrush(Color.Parse("#F4F4F6")) };

    public IBrush Normal { get; init; } = Brushes.White;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => EqualsMultiConverter.Instance.Convert(values, targetType, parameter, culture) is true ? Accent : Normal;
}
