using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlattaPlayer.Visualizations.MilkDrop.Presets;

/// <summary>
/// A directory of <c>.milk</c> preset files the visualizer can cycle through. The library only holds the
/// file paths; a preset is parsed lazily when it is actually selected, so a folder of thousands of presets
/// is cheap to open. Parsing is fault-tolerant: a malformed file is skipped (the next one is tried) rather
/// than throwing, so one bad preset never stalls the show.
///
/// Pure managed I/O (no GPU/Avalonia), so it lives in the engine assembly and any rendering head can drive
/// it. The library is not thread-safe; drive it from the render thread.
/// </summary>
public sealed class PresetLibrary
{
    private readonly string[] _files;
    private readonly Random _rng;
    private int _index = -1;

    private PresetLibrary(string[] files, Random rng)
    {
        _files = files;
        _rng = rng;
    }

    /// <summary>Number of preset files discovered.</summary>
    public int Count => _files.Length;

    /// <summary>True when the directory yielded at least one preset file.</summary>
    public bool IsEmpty => _files.Length == 0;

    /// <summary>Path of the most recently selected preset, or null before the first selection.</summary>
    public string? CurrentPath => _index >= 0 && _index < _files.Length ? _files[_index] : null;

    /// <summary>
    /// Scans <paramref name="directory"/> (non-recursively unless <paramref name="recursive"/>) for
    /// <c>.milk</c> files, sorted by name for stable ordering. A missing directory yields an empty library
    /// rather than throwing. <paramref name="seed"/> seeds the shuffle RNG for reproducible tests.
    /// </summary>
    public static PresetLibrary FromDirectory(string? directory, bool recursive = false, int? seed = null)
    {
        var rng = seed is { } s ? new Random(s) : new Random();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new PresetLibrary([], rng);

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.milk", option)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            files = [];
        }
        catch (UnauthorizedAccessException)
        {
            files = [];
        }

        return new PresetLibrary(files, rng);
    }

    /// <summary>
    /// Advances by <paramref name="direction"/> steps (wrapping) and parses the resulting preset. Files
    /// that fail to parse are skipped in the same direction; returns null only if every remaining file is
    /// unreadable. <paramref name="direction"/> is typically +1 (next) or -1 (previous).
    /// </summary>
    public MilkdropPreset? Advance(int direction = 1)
    {
        if (_files.Length == 0) return null;
        var step = direction >= 0 ? 1 : -1;

        for (var tried = 0; tried < _files.Length; tried++)
        {
            _index = ((_index + step) % _files.Length + _files.Length) % _files.Length;
            if (TryParse(_files[_index]) is { } preset) return preset;
        }
        return null;
    }

    /// <summary>Jumps to a random preset (never throwing on a bad file; skips to the next readable one).</summary>
    public MilkdropPreset? Random()
    {
        if (_files.Length == 0) return null;
        _index = _rng.Next(_files.Length) - 1; // Advance(+1) lands on the chosen index
        return Advance(1);
    }

    private static MilkdropPreset? TryParse(string path)
    {
        try
        {
            return MilkParser.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}
