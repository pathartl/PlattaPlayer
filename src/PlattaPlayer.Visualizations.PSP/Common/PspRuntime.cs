using System.Diagnostics;
using PlattaPlayer.Visualizations.PSP.Gu;

namespace PlattaPlayer.Visualizations.PSP.Common;

/// <summary>An image decoded by paf::Image: tightly packed 32-bit pixels, R in the lowest byte, pitch = width.</summary>
public sealed record PafImage(int Width, int Height, uint[] Pixels);

/// <summary>Where the visualizers get the resources that are not code: textures and embedded images.</summary>
public interface IPspAssets
{
    /// <summary>
    /// A texture from visualizer_plugin.rco by resource name, e.g. "music_tex_led_red_norml" (a leading
    /// '/' is ignored), decoded to RGBA8888, or null if there is no such texture. The same instance is
    /// returned on every call.
    /// </summary>
    PafSurface? GetRcoTexture(string name);

    /// <summary>The 64x64 JPEG embedded in visualizer_plugin.prx at 0x13850 (0x55f bytes), read by the type 3 clouds.</summary>
    ReadOnlyMemory<byte> CloudPaletteJpeg { get; }

    /// <summary>The 120x68 JPEG embedded in visualizer_plugin.prx at 0x13e00, decoded (and ignored) by type 7.</summary>
    ReadOnlyMemory<byte> SandTrailsJpeg { get; }
}

/// <summary>
/// The PSP services a visualizer reaches through paf.prx, the kernel and plugin globals
/// (src/platform/paf.h and the per-visualizer *_platform.h headers), gathered so that every visualizer
/// instance has its own: GU, paf's rand(), the player state, the system timer, the RCO textures and the
/// JPEG decoder.
/// </summary>
public sealed class PspRuntime
{
    private readonly Func<ReadOnlyMemory<byte>, PafImage?> _decodeJpeg;
    private readonly long _start = Stopwatch.GetTimestamp();
    private uint _randState = 1;

    /// <param name="assets">Textures and embedded images extracted from the firmware.</param>
    /// <param name="decodeJpeg">paf::Image::Open(JPEG) + ToBuffer: returns null when the data does not decode.
    /// Defaults to the managed <see cref="BaselineJpeg"/> decoder.</param>
    public PspRuntime(IPspAssets assets, Func<ReadOnlyMemory<byte>, PafImage?>? decodeJpeg = null)
    {
        Assets = assets;
        _decodeJpeg = decodeJpeg ?? BaselineJpeg.Decode;
    }

    public GuContext Gu { get; } = new();

    public IPspAssets Assets { get; }

    /// <summary>
    /// g_playerState (DAT_00014ae0), written by the controller's event handler (0x1f98). Event type ->
    /// state: 0->0, 1->4, 2->0, 3->2, 4->3, 5->1. 0 = playing; 1 = no audio (several visualizers flatten
    /// their input); 2/3 look like fast-forward / rewind.
    /// </summary>
    public int PlayerState { get; set; }

    /// <summary>
    /// paf::Framework::Instance()->+0x9c: nonzero when output is to a 16:9 TV, which makes several
    /// visualizers widen their 480-unit layout to 564.7059 units.
    /// </summary>
    public bool IsWideOutput { get; set; }

    /// <summary>sce_paf_private_rand: LCG s = s*0x41C64E6D + 0x3039; returns (s >> 16) &amp; 0x7fff.</summary>
    public int Rand()
    {
        _randState = unchecked(_randState * 0x41C64E6Du + 0x3039u);
        return (int)(_randState >> 16 & 0x7fff);
    }

    /// <summary>sce_paf_private_srand</summary>
    public void Srand(uint seed) => _randState = seed;

    /// <summary>sceKernelGetSystemTimeLow: microsecond timer, low 32 bits.</summary>
    public uint SystemTimeLow() =>
        unchecked((uint)(Stopwatch.GetElapsedTime(_start).Ticks / (TimeSpan.TicksPerMillisecond / 1000)));

    /// <summary>paf::Image::Open(data, size, null, kImageFormatJpeg) + ToBuffer(true).</summary>
    public PafImage? OpenJpeg(ReadOnlyMemory<byte> data) => data.IsEmpty ? null : _decodeJpeg(data);
}
