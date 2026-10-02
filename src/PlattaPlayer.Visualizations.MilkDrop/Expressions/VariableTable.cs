using System;
using System.Collections.Generic;

namespace PlattaPlayer.Visualizations.MilkDrop.Expressions;

/// <summary>
/// Interns variable names to integer slots backed by a flat <see cref="double"/> array. All equation
/// blocks of a single preset (init / per-frame / per-pixel / wave / shape) share one table so they
/// communicate through the same named registers (e.g. <c>q1</c>..<c>q32</c>, user vars). Resolving a
/// name to a slot once at parse time keeps per-pixel evaluation (run thousands of times per frame) off
/// the dictionary hot path. Names are matched case-insensitively because MilkDrop lowercases preset
/// code before compiling.
/// </summary>
public sealed class VariableTable
{
    private readonly Dictionary<string, int> _slots = new(StringComparer.OrdinalIgnoreCase);
    private double[] _values = new double[64];
    private int _count;

    /// <summary>Returns the slot for <paramref name="name"/>, allocating one on first use.</summary>
    public int Intern(string name)
    {
        if (_slots.TryGetValue(name, out var slot)) return slot;
        slot = _count++;
        if (slot >= _values.Length) Array.Resize(ref _values, _values.Length * 2);
        _slots[name] = slot;
        return slot;
    }

    /// <summary>Slot for an existing name, or -1 if the preset never referenced it.</summary>
    public int Find(string name) => _slots.TryGetValue(name, out var slot) ? slot : -1;

    public double Get(int slot) => _values[slot];

    public void Set(int slot, double value) => _values[slot] = value;

    /// <summary>Convenience for host code: write a built-in by name (no-op if unreferenced).</summary>
    public void Set(string name, double value)
    {
        var slot = Find(name);
        if (slot >= 0) _values[slot] = value;
    }

    /// <summary>Convenience for host code: read an output by name (0 if unreferenced).</summary>
    public double Get(string name)
    {
        var slot = Find(name);
        return slot >= 0 ? _values[slot] : 0.0;
    }

    public double[] Values => _values;
}
