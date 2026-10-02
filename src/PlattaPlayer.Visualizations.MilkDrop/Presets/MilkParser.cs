using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace PlattaPlayer.Visualizations.MilkDrop.Presets;

/// <summary>
/// Parses the INI-style MilkDrop <c>.milk</c> preset format. The format stores equation blocks as many
/// numbered single-line keys (<c>per_frame_1</c>, <c>per_frame_2</c>, …) which we sort and re-join into
/// one source string per block, and pixel-shader source as backtick-prefixed <c>warp_N</c>/<c>comp_N</c>
/// lines. Everything else that looks like <c>key=number</c> becomes a base value.
/// </summary>
public static partial class MilkParser
{
    [GeneratedRegex(@"^per_frame_init_(\d+)$")] private static partial Regex FrameInitKey();
    [GeneratedRegex(@"^per_frame_(\d+)$")] private static partial Regex FrameKey();
    [GeneratedRegex(@"^per_pixel_(\d+)$")] private static partial Regex PixelKey();
    [GeneratedRegex(@"^warp_(\d+)$")] private static partial Regex WarpKey();
    [GeneratedRegex(@"^comp_(\d+)$")] private static partial Regex CompKey();
    [GeneratedRegex(@"^wavecode_(\d+)_(.+)$")] private static partial Regex WaveValueKey();
    [GeneratedRegex(@"^wave_(\d+)_(init|per_frame|per_point)\d+$")] private static partial Regex WaveCodeKey();
    [GeneratedRegex(@"^shapecode_(\d+)_(.+)$")] private static partial Regex ShapeValueKey();
    [GeneratedRegex(@"^shape_(\d+)_(init|per_frame)\d+$")] private static partial Regex ShapeCodeKey();

    public static MilkdropPreset ParseFile(string path)
        => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public static MilkdropPreset Parse(string text, string name)
    {
        var baseValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var init = new SortedDictionary<int, string>();
        var frame = new SortedDictionary<int, string>();
        var pixel = new SortedDictionary<int, string>();
        var warp = new SortedDictionary<int, string>();
        var comp = new SortedDictionary<int, string>();
        var waves = new Dictionary<int, CustomBlockBuilder>();
        var shapes = new Dictionary<int, CustomBlockBuilder>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..];

            if (TryIndexed(FrameInitKey(), key, out var n)) { init[n] = value; continue; }
            if (TryIndexed(FrameKey(), key, out n)) { frame[n] = value; continue; }
            if (TryIndexed(PixelKey(), key, out n)) { pixel[n] = value; continue; }
            if (TryIndexed(WarpKey(), key, out n)) { warp[n] = StripBacktick(value); continue; }
            if (TryIndexed(CompKey(), key, out n)) { comp[n] = StripBacktick(value); continue; }

            if (WaveValueKey().Match(key) is { Success: true } wv)
            { Builder(waves, int.Parse(wv.Groups[1].Value)).AddValue(wv.Groups[2].Value, value); continue; }
            if (WaveCodeKey().Match(key) is { Success: true } wc)
            { Builder(waves, int.Parse(wc.Groups[1].Value)).AddCode(wc.Groups[2].Value, key, value); continue; }
            if (ShapeValueKey().Match(key) is { Success: true } sv)
            { Builder(shapes, int.Parse(sv.Groups[1].Value)).AddValue(sv.Groups[2].Value, value); continue; }
            if (ShapeCodeKey().Match(key) is { Success: true } sc)
            { Builder(shapes, int.Parse(sc.Groups[1].Value)).AddCode(sc.Groups[2].Value, key, value); continue; }

            if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                baseValues[key] = num;
        }

        return new MilkdropPreset
        {
            Name = name,
            BaseValues = baseValues,
            InitCode = JoinCode(init),
            FrameCode = JoinCode(frame),
            PixelCode = JoinCode(pixel),
            WarpShader = JoinShader(warp),
            CompShader = JoinShader(comp),
            Waves = BuildBlocks(waves),
            Shapes = BuildBlocks(shapes),
        };
    }

    private static bool TryIndexed(Regex regex, string key, out int index)
    {
        var m = regex.Match(key);
        if (m.Success) { index = int.Parse(m.Groups[1].Value); return true; }
        index = 0;
        return false;
    }

    // Each numbered line is one statement; re-join with ';' so adjacent lines never merge.
    private static string JoinCode(SortedDictionary<int, string> lines)
        => lines.Count == 0 ? string.Empty : string.Join(";\n", lines.Values);

    private static string JoinShader(SortedDictionary<int, string> lines)
        => lines.Count == 0 ? string.Empty : string.Join("\n", lines.Values);

    private static string StripBacktick(string value)
        => value.StartsWith('`') ? value[1..] : value;

    private static CustomBlockBuilder Builder(Dictionary<int, CustomBlockBuilder> map, int index)
    {
        if (!map.TryGetValue(index, out var b)) map[index] = b = new CustomBlockBuilder(index);
        return b;
    }

    private static List<CustomCodeBlock> BuildBlocks(Dictionary<int, CustomBlockBuilder> map)
    {
        var list = new List<CustomCodeBlock>(map.Count);
        foreach (var key in new SortedSet<int>(map.Keys)) list.Add(map[key].Build());
        return list;
    }

    /// <summary>Accumulates the scattered keys of one custom wave/shape slot before building it.</summary>
    private sealed class CustomBlockBuilder(int index)
    {
        private readonly Dictionary<string, double> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedDictionary<int, string> _init = new();
        private readonly SortedDictionary<int, string> _frame = new();
        private readonly SortedDictionary<int, string> _point = new();

        public void AddValue(string subKey, string value)
        {
            if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                _values[subKey] = num;
        }

        public void AddCode(string kind, string fullKey, string value)
        {
            var n = TrailingNumber(fullKey);
            switch (kind)
            {
                case "init": _init[n] = value; break;
                case "per_frame": _frame[n] = value; break;
                case "per_point": _point[n] = value; break;
            }
        }

        public CustomCodeBlock Build() => new()
        {
            Index = index,
            Values = _values,
            InitCode = JoinCode(_init),
            FrameCode = JoinCode(_frame),
            PointCode = JoinCode(_point),
        };

        private static int TrailingNumber(string key)
        {
            var i = key.Length;
            while (i > 0 && char.IsDigit(key[i - 1])) i--;
            return int.TryParse(key.AsSpan(i), out var n) ? n : 0;
        }
    }
}
