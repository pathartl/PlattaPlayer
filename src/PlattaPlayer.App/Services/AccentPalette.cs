using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PlattaPlayer.App.Services;

/// <summary>
/// Applies the user's accent color to the app's resources at runtime. The accent brushes from
/// Themes/Colors.axaml are recolored in place, so every <c>StaticResource</c> use of them updates live; the
/// Fluent <c>SystemAccentColor*</c> keys are overridden so stock controls (check boxes, focus rings) follow.
/// </summary>
public static class AccentPalette
{
    /// <summary>The design's mint, used when no accent has been chosen.</summary>
    public static readonly Color DefaultColor = Color.Parse("#6FF2D2");

    /// <summary>The saturated swatches offered in Settings (any other color comes from the custom picker).</summary>
    public static readonly IReadOnlyList<(string Name, Color Color)> Presets =
    [
        ("Mint", DefaultColor),
        ("Aqua", Color.Parse("#00E5FF")),
        ("Blue", Color.Parse("#2979FF")),
        ("Violet", Color.Parse("#8C3CFF")),
        ("Magenta", Color.Parse("#FF2BD6")),
        ("Red", Color.Parse("#FF1F4B")),
        ("Orange", Color.Parse("#FF7A00")),
        ("Yellow", Color.Parse("#FFD600")),
        ("Lime", Color.Parse("#76FF03")),
    ];

    /// <summary>Parses <c>#RRGGBB</c> / <c>RRGGBB</c> (alpha is not allowed: the accent is always opaque).</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        color = Color.FromUInt32(0xFF000000 | rgb);
        return true;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>The persisted accent, or the default when unset/invalid.</summary>
    public static Color Resolve(string? stored) => TryParse(stored, out var c) ? c : DefaultColor;

    public static void Apply(Color accent)
    {
        if (Application.Current?.Resources is not { } resources) return;

        var onAccent = Luminance(accent) > 0.35 ? Mix(accent, Colors.Black, 0.93) : Colors.White;

        resources["AccentColor"] = accent;
        resources["OnAccentColor"] = onAccent;
        SetBrush(resources, "AccentBrush", accent);
        SetBrush(resources, "AccentHoverBrush", Mix(accent, Colors.White, 0.2));
        SetBrush(resources, "OnAccentBrush", onAccent);
        SetBrush(resources, "SliderTrackValueFill", accent);
        SetBrush(resources, "SliderTrackValueFillPointerOver", accent);
        SetBrush(resources, "SliderTrackValueFillPressed", accent);

        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.15);
        resources["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.3);
        resources["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.45);
        resources["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.15);
        resources["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.3);
        resources["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.45);
    }

    private static void SetBrush(IResourceDictionary resources, string key, Color color)
    {
        // Recolor the shared instance so StaticResource consumers update; replace it only if it isn't mutable.
        if (resources.TryGetResource(key, null, out var existing) && existing is SolidColorBrush brush)
            brush.Color = color;
        else
            resources[key] = new SolidColorBrush(color);
    }

    private static Color Mix(Color from, Color to, double t) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * t + 0.5),
        (byte)(from.G + (to.G - from.G) * t + 0.5),
        (byte)(from.B + (to.B - from.B) * t + 0.5));

    // Rec. 709 luma on linearized channels; decides dark vs white text on the accent.
    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
