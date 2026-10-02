using System;
using System.Collections.Generic;
using System.Globalization;
using PlattaPlayer.Core.Models;

namespace PlattaPlayer.App.Formatting;

/// <summary>
/// Display strings for audio technical data, in the design's notation: "24/192" (bit depth / kHz) for PCM and
/// lossless, "320" (kbps) for lossy, "FLAC 24/96" on album tiles and "24-bit / 192 kHz" in badges. Empty when
/// the library has no data for the track (e.g. scanned before these fields existed, or a remote source).
/// </summary>
public static class AudioFormatText
{
    private static readonly HashSet<string> Lossless =
        new(StringComparer.OrdinalIgnoreCase) { "flac", "alac", "wav", "wave", "aiff", "aif", "ape", "wv", "tak", "tta", "dsf", "dff" };

    public static bool IsLossless(Track t) => Lossless.Contains(t.Format);

    /// <summary>Upper-case container/codec, e.g. "FLAC", "MIDI".</summary>
    public static string Codec(Track t) => t.Format.ToUpperInvariant();

    /// <summary>"24/192", "16/44.1", "320" or "".</summary>
    public static string Short(Track t)
    {
        if (t.BitsPerSample is { } bits && t.SampleRate is { } rate)
            return $"{bits}/{Kilohertz(rate)}";
        if (!IsLossless(t) && t.Bitrate is { } kbps)
            return kbps.ToString(CultureInfo.InvariantCulture);
        if (t.SampleRate is { } r)
            return Kilohertz(r);
        return string.Empty;
    }

    /// <summary>"FLAC 24/96", "MP3 320", or just the codec.</summary>
    public static string WithCodec(Track t) => $"{Codec(t)} {Short(t)}".Trim();

    /// <summary>"24-bit / 192 kHz", "44.1 kHz · 320 kbps", or "".</summary>
    public static string Long(Track t)
    {
        if (t.BitsPerSample is { } bits && t.SampleRate is { } rate)
            return $"{bits}-bit / {Kilohertz(rate)} kHz";
        var parts = new List<string>();
        if (t.SampleRate is { } r) parts.Add($"{Kilohertz(r)} kHz");
        if (t.Bitrate is { } kbps) parts.Add($"{kbps} kbps");
        return string.Join(" · ", parts);
    }

    /// <summary>44100 → "44.1", 48000 → "48", 192000 → "192".</summary>
    public static string Kilohertz(int hz)
        => (hz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>"4:57", or "1:02:10" past an hour.</summary>
    public static string Duration(TimeSpan t)
        => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>"47 min", "4 h 12 min".</summary>
    public static string Runtime(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

    /// <summary>"1 album" / "3 albums".</summary>
    public static string Count(int n, string singular, string plural)
        => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? singular : plural)}";
}
