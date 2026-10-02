using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PlattaPlayer.App.Converters;

/// <summary>
/// Width of a slim slider's filled part: [value, minimum, maximum, trackWidth] → the distance from the left
/// edge to the thumb's centre (the thumb is <see cref="ThumbSize"/> wide and travels within the track).
/// </summary>
public sealed class SliderFillConverter : IMultiValueConverter
{
    public const double ThumbSize = 12;

    public static readonly SliderFillConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 4 || values[0] is not double value || values[1] is not double min
            || values[2] is not double max || values[3] is not double width || max <= min || width <= 0)
            return 0d;

        var fraction = Math.Clamp((value - min) / (max - min), 0, 1);
        return fraction <= 0 ? 0d : fraction * (width - ThumbSize) + ThumbSize / 2;
    }
}
