using System;
using System.Collections.Generic;

namespace PlattaPlayer.Visualizations.MilkDrop.Expressions;

/// <summary>
/// The mutable state an <see cref="EelProgram"/> reads and writes while executing: the shared variable
/// slots, the per-instance (<c>megabuf</c>) and global (<c>gmegabuf</c>) sparse memories, and the RNG
/// behind <c>rand</c>. One context is shared by every equation block of a preset so they see the same
/// variables; the global memory is shared across the whole engine so presets can persist state.
/// </summary>
public sealed class EvalContext
{
    public EvalContext(VariableTable variables, Dictionary<int, double> globalMemory)
    {
        Variables = variables;
        GlobalMemory = globalMemory;
    }

    public VariableTable Variables { get; }

    /// <summary>Per-instance memory addressed by <c>megabuf(i)</c> / <c>i[j]</c>; unset slots read 0.</summary>
    public Dictionary<int, double> LocalMemory { get; } = new();

    /// <summary>Engine-wide memory addressed by <c>gmegabuf(i)</c>; unset slots read 0.</summary>
    public Dictionary<int, double> GlobalMemory { get; }

    public Random Rng { get; } = new();

    public double GetVar(int slot) => Variables.Get(slot);
    public void SetVar(int slot, double value) => Variables.Set(slot, value);

    public static double ReadMemory(Dictionary<int, double> mem, double index)
    {
        var i = (int)index;
        return i >= 0 && mem.TryGetValue(i, out var v) ? v : 0.0;
    }

    public static double WriteMemory(Dictionary<int, double> mem, double index, double value)
    {
        var i = (int)index;
        if (i >= 0) mem[i] = value;
        return value;
    }
}
