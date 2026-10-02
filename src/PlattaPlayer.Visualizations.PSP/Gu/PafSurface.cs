namespace PlattaPlayer.Visualizations.PSP.Gu;

/// <summary>
/// paf::Surface: a pixel buffer that can be bound as a GU texture (original 0x68 bytes, refcounted
/// through SurfaceRCPtr 0x1031c / 0x1033c / 0x10910). Only RGBA8888 is needed by the visualizers:
/// surfaces they create are 8888, and textures from visualizer_plugin.rco are decoded to 8888 on load.
/// Pixels are 0xAABBGGRR (R in the low byte), row pitch = <see cref="BufferWidth"/>.
/// </summary>
public sealed class PafSurface
{
    /// <summary>paf "ImageMode", matching the GU pixel formats.</summary>
    public const int Mode8888 = 3;

    /// <summary>
    /// paf_Surface_new + paf_Surface_ctor (0xAD454A8F / 0xA5BBFD24), called in the plugin as
    /// ctor(surf, w, h, mode, order, flag, a5, pixels, a7). Only 8888 is supported. The row pitch is the
    /// width: type 7 copies into its 480x273 surface with a fixed 480 stride.
    /// </summary>
    public PafSurface(int width, int height, int mode = Mode8888, uint[]? pixels = null)
    {
        if (mode != Mode8888) throw new NotSupportedException($"paf surface mode {mode} is not emulated.");
        Width = width;
        Height = height;
        Mode = mode;
        BufferWidth = width;
        Pixels = new uint[width * height];
        pixels?.AsSpan(0, Math.Min(pixels.Length, Pixels.Length)).CopyTo(Pixels);
    }

    /// <summary>Surface +0x18 (short).</summary>
    public int Width { get; }

    /// <summary>Surface +0x1a (short).</summary>
    public int Height { get; }

    public int Mode { get; }

    public int BufferWidth { get; }

    public uint[] Pixels { get; }

    /// <summary>Bumped on every <see cref="Unlock"/>, so the renderer knows to re-upload.</summary>
    public int Version { get; private set; }

    /// <summary>Non-zero while locked. A locked surface may be rewritten at any time (type 7 keeps its
    /// canvas locked for its whole lifetime), so binding one always counts as a change.</summary>
    public int LockCount { get; private set; }

    /// <summary>Name of the RCO resource it was loaded from, or null for surfaces made at runtime.</summary>
    public string? RcoName { get; init; }

    /// <summary>Surface::Lock(0): the pixels, for CPU access.</summary>
    public uint[] Lock(int flags = 0)
    {
        LockCount++;
        return Pixels;
    }

    /// <summary>Surface::Unlock()</summary>
    public void Unlock()
    {
        if (LockCount > 0) LockCount--;
        Version++;
    }

    /// <summary>Marks the pixels as changed (used by the GU recorder when a locked surface is bound).</summary>
    internal void Touch() => Version++;
}
