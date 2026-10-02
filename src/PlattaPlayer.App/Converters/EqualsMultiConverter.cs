using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PlattaPlayer.App.Converters;

/// <summary>
/// True when the first two bound values are equal (and not 0/null). Used to mark the playing artist/album/track
/// (<c>item.Id == NowPlaying.ArtistId</c>) and the selected nav entry without per-item subscriptions.
/// </summary>
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public static readonly EqualsMultiConverter Instance = new();

    /// <summary>The negation: true when the values differ (e.g. show the track number unless playing).</summary>
    public static readonly EqualsMultiConverter Not = new() { Negate = true };

    public bool Negate { get; init; }

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var equal = values.Count >= 2 && values[0] is not (null or 0) && values[1] is not (null or 0)
                    && Equals(values[0], values[1]);
        return equal != Negate;
    }
}
