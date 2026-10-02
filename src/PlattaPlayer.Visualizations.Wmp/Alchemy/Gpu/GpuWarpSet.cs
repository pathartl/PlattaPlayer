using System;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// One warp kernel as the shader sees it: its kind, its parameters, its out-of-range policy, and, for
/// OScope, the embedded kernel it delegates to.
/// </summary>
public sealed class GpuWarpSlot
{
    public GpuWarpKind Kind;
    public bool ToOrigin;
    public readonly float[] Params = new float[GpuWarp.ParamCapacity];
    public GpuWarpKind ChildKind;
    public readonly float[] ChildParams = new float[GpuWarp.ParamCapacity];

    /// <summary>
    /// The kernel instance this slot was harvested from. The scaler uses it to tell a kernel that keeps
    /// running from a new one, which restarts its sub-pixel carry.
    /// </summary>
    public object? Source;

    public void Clear()
    {
        Kind = GpuWarpKind.None;
        ToOrigin = false;
        ChildKind = GpuWarpKind.None;
        Array.Clear(Params);
        Array.Clear(ChildParams);
        Source = null;
    }

    public void CopyFrom(GpuWarpSlot other)
    {
        Kind = other.Kind;
        ToOrigin = other.ToOrigin;
        ChildKind = other.ChildKind;
        Array.Copy(other.Params, Params, Params.Length);
        Array.Copy(other.ChildParams, ChildParams, ChildParams.Length);
        Source = other.Source;
    }
}

/// <summary>
/// The warp table the feedback pass reads, as parameters instead of a per-pixel map: kernel A, and the
/// optional kernel B that <see cref="WarpMap.Build"/> applies on top and averages back toward the
/// identity.
/// </summary>
public sealed class GpuWarpSet
{
    public readonly GpuWarpSlot A = new();
    public readonly GpuWarpSlot B = new();
    public bool HasB;

    public void Clear()
    {
        A.Clear();
        B.Clear();
        HasB = false;
    }

    public void CopyFrom(GpuWarpSet other)
    {
        A.CopyFrom(other.A);
        B.CopyFrom(other.B);
        HasB = other.HasB;
    }
}
