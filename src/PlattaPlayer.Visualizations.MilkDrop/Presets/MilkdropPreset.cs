using System.Collections.Generic;

namespace PlattaPlayer.Visualizations.MilkDrop.Presets;

/// <summary>
/// A parsed MilkDrop <c>.milk</c> preset: the scalar "base" values (decay, gamma, wave colours, …),
/// the three core equation blocks (init / per-frame / per-pixel) as raw source, and the raw warp/comp
/// pixel-shader source for the MilkDrop2 path. Custom waves and shapes are captured as raw blocks for a
/// later rendering phase. Equation source is compiled by the engine, not here, so a preset object is a
/// cheap, immutable description.
/// </summary>
public sealed class MilkdropPreset
{
    public required string Name { get; init; }

    /// <summary>All <c>key=number</c> entries (keys lowercased), e.g. <c>fdecay</c>, <c>zoom</c>, <c>rot</c>.</summary>
    public required IReadOnlyDictionary<string, double> BaseValues { get; init; }

    public string InitCode { get; init; } = string.Empty;
    public string FrameCode { get; init; } = string.Empty;
    public string PixelCode { get; init; } = string.Empty;

    /// <summary>Raw HLSL warp shader source (MilkDrop2 presets), empty for classic presets.</summary>
    public string WarpShader { get; init; } = string.Empty;

    /// <summary>Raw HLSL composite shader source (MilkDrop2 presets), empty for classic presets.</summary>
    public string CompShader { get; init; } = string.Empty;

    /// <summary>Custom waveform blocks (raw source), indexed by their preset slot.</summary>
    public IReadOnlyList<CustomCodeBlock> Waves { get; init; } = [];

    /// <summary>Custom shape blocks (raw source), indexed by their preset slot.</summary>
    public IReadOnlyList<CustomCodeBlock> Shapes { get; init; } = [];

    /// <summary>True when the preset carries pixel-shader source (MilkDrop2), false for classic.</summary>
    public bool HasShaders => WarpShader.Length > 0 || CompShader.Length > 0;

    public double GetBase(string key, double fallback)
        => BaseValues.TryGetValue(key, out var v) ? v : fallback;
}

/// <summary>Raw source + scalar settings for one custom wave or shape slot, kept for a later phase.</summary>
public sealed class CustomCodeBlock
{
    public required int Index { get; init; }
    public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>();
    public string InitCode { get; init; } = string.Empty;
    public string FrameCode { get; init; } = string.Empty;
    public string PointCode { get; init; } = string.Empty;
}
