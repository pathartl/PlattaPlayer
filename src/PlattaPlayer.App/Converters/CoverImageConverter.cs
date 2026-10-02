using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace PlattaPlayer.App.Converters;

/// <summary>
/// Converts an absolute cover-art file path into a decoded <see cref="Bitmap"/>. Returns null when no art is
/// available, which lets the placeholder behind the image show through.
///
/// Two usage shapes are supported:
/// <list type="bullet">
/// <item>Single value with an optional integer <c>ConverterParameter</c> (e.g. <c>ConverterParameter=1024</c>)
/// for fixed-size surfaces like the Now Playing page.</item>
/// <item>A <see cref="IMultiValueConverter"/> <c>MultiBinding</c> of <c>[coverPath, decodePixelWidth]</c> used by
/// the responsive cover grid, so each tile decodes at the exact pixel size it is rendered at.</item>
/// </list>
/// Decode widths are snapped up to the nearest 20px so minor layout shifts reuse the same cache entry, and the
/// shared cache is bounded to <see cref="BitmapCache.MaxBytes"/> of decoded pixels with least-recently-used eviction.
/// </summary>
public sealed class CoverImageConverter : IValueConverter, IMultiValueConverter
{
    public static readonly CoverImageConverter Instance = new();

    /// <summary>Fallback decode width in pixels when no size is supplied by the binding.</summary>
    public int DecodeWidth { get; set; } = 320;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
            return null;

        return BitmapCache.Get(path, ParseWidth(parameter, DecodeWidth));
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not string path || string.IsNullOrEmpty(path))
            return null;

        var width = values.Count > 1 ? ParseWidth(values[1], DecodeWidth) : DecodeWidth;
        return BitmapCache.Get(path, width);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static int ParseWidth(object? value, int fallback) => value switch
    {
        int w when w > 0 => w,
        double d when d > 0 => (int)Math.Ceiling(d),
        long l when l > 0 => (int)l,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w > 0 => w,
        _ => fallback
    };
}

/// <summary>
/// Process-wide, byte-bounded LRU cache of decoded cover bitmaps, keyed by (path, snapped width). Sizing the cache
/// by decoded pixel bytes (not entry count) keeps memory predictable regardless of how many distinct sizes are
/// requested. Evicted bitmaps are merely dropped from the cache, not disposed, since live <c>Image</c> controls may
/// still reference them; the GC reclaims them once nothing does.
/// </summary>
internal static class BitmapCache
{
    /// <summary>Upper bound on total decoded pixel bytes held in the cache.</summary>
    public const long MaxBytes = 128L * 1024 * 1024;

    /// <summary>Decode widths are rounded up to this granularity so near-identical layouts share an entry.</summary>
    private const int SnapTo = 20;

    private sealed class Entry
    {
        public required (string Path, int Width) Key { get; init; }
        public required Bitmap? Bitmap { get; init; }
        public required long Bytes { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<(string, int), LinkedListNode<Entry>> Map = new();
    private static readonly LinkedList<Entry> Lru = new(); // most-recently-used at the front
    private static long _bytes;

    public static Bitmap? Get(string path, int requestedWidth)
    {
        var width = Snap(requestedWidth);
        var key = (path, width);

        lock (Gate)
        {
            if (Map.TryGetValue(key, out var hit))
            {
                Lru.Remove(hit);
                Lru.AddFirst(hit);
                return hit.Value.Bitmap;
            }
        }

        // Decode outside the lock so a slow decode never stalls other tiles. A rare duplicate decode on a
        // simultaneous miss is acceptable; the second insert simply replaces the first.
        var bitmap = Decode(path, width);
        var bytes = bitmap is null ? 0 : (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        var entry = new Entry { Key = key, Bitmap = bitmap, Bytes = bytes };

        lock (Gate)
        {
            if (Map.TryGetValue(key, out var existing))
            {
                Lru.Remove(existing);
                Lru.AddFirst(existing);
                return existing.Value.Bitmap;
            }

            var node = new LinkedListNode<Entry>(entry);
            Map[key] = node;
            Lru.AddFirst(node);
            _bytes += bytes;
            Evict();
            return bitmap;
        }
    }

    private static void Evict()
    {
        while (_bytes > MaxBytes && Lru.Last is { } oldest && Map.Count > 1)
        {
            Lru.RemoveLast();
            Map.Remove(oldest.Value.Key);
            _bytes -= oldest.Value.Bytes;
        }
    }

    private static int Snap(int width)
    {
        if (width < SnapTo) width = SnapTo;
        return (width + SnapTo - 1) / SnapTo * SnapTo;
    }

    private static Bitmap? Decode(string path, int width)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch
        {
            return null;
        }
    }
}
