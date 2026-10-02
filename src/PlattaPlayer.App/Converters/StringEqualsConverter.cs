using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PlattaPlayer.App.Converters;

/// <summary>Returns true when the bound value equals the converter parameter (used for nav highlighting).</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
