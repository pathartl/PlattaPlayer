using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Visualizations.Wmp.Battery.Presets;

namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// <c>CBattery</c>: the whole effect. It owns the render data (8-bit field, palette, effect pools), the
/// 26 presets (Randomization plus the 25 shipped recipes), and the per-frame state machine:
/// play renders, pause holds, and stop fades the field to palette index 1 over 300 frames.
///
/// This is a port of wmp.dll 12.0.26100.9278 x64, verified piece by piece against the real code by the
/// harness's <c>verify-battery-*</c> oracles. The original's field is fixed at 384×288. It
/// presents it with <c>StretchBlt(COLORONCOLOR)</c>, i.e. nearest-neighbour replication; that is left to
/// the host. Derivation: <c>D:\Decompilation\Windows Media Player\Code\battery\</c>.
///
/// The GPU path runs this same class at another field size (<see cref="Resize"/>, the original's own
/// resolution-change path) with its pixel stages replaced by a recorder (<see cref="IBatteryRaster"/>),
/// so every decision and every rand() draw still happens here.
/// </summary>
public sealed class BatteryCore
{
    public const int FieldWidth = 384;
    public const int FieldHeight = 288;

    private readonly List<BatteryPreset> _presets = [];
    private readonly BatteryLevels _levels = new();
    private bool _allocated;
    private int _fade;
    private int _current;

    /// <summary>
    /// Builds the effect the way its creator does. The render-effect constructors draw from
    /// <paramref name="rand"/> first (CJDar 6, CGalaxy 2). Only then does it <c>srand(seed)</c>, unless
    /// <paramref name="seed"/> is null (the oracle drives a scripted stream instead).
    /// </summary>
    /// <param name="useTransitions">CRenderData+0x5a. WMP defaults it on for any machine with more than
    /// about 500 MB of RAM (registry <c>PlayerTransitions</c> overrides).</param>
    /// <param name="raster">Replaces the CPU pixel stages (the GPU path's recorder). It draws nothing
    /// from rand().</param>
    public BatteryCore(CrtRand? rand = null, uint? seed = null, bool useTransitions = true, RegistryKeyData? presets = null,
        int width = FieldWidth, int height = FieldHeight, Func<BatteryRenderData, IBatteryRaster>? raster = null)
    {
        Rand = rand ?? new CrtRand();
        RenderData = new BatteryRenderData(Rand, width, height) { UseTransitions = useTransitions };
        if (raster is not null) RenderData.Raster = raster(RenderData);
        if (seed is { } s) Rand.Seed(s);

        // The initial palette is a blue ramp: entry i = (0, 0, i).
        for (var i = 0; i < 256; i++) RenderData.Palette.Current[i] = (uint)i << 16;

        _presets.Add(new RandomPreset(RenderData));
        var root = presets ?? RegistryDump.Embedded;
        foreach (var key in root.SubKey("Presets")?.SubKeys ?? [])
            _presets.Add(new SavedPreset(RenderData, key));

        _presets[0].Allocate(Rand); // FinalConstruct
    }

    public CrtRand Rand { get; }

    public BatteryRenderData RenderData { get; }

    /// <summary>The palette DirectDraw shows. It is snapshotted from <see cref="BatteryPalette.Current"/>
    /// on allocation and refreshed whenever a transition steps. The upload covers entries 0..254 only
    /// (<c>SetEntries(0, 0, 255, ...)</c>), so entry 255 keeps its initial value.</summary>
    public uint[] DisplayPalette { get; } = new uint[256];

    /// <summary>
    /// The palette the presented frame actually shows. The surface's GDI colour table is taken when
    /// Render calls <c>GetDC</c>, which happens BEFORE <c>RenderEffect</c> uploads this frame's
    /// palette step, so the screen always runs one palette step behind <see cref="DisplayPalette"/>.
    /// This was measured against the real effect by <c>verify-battery</c>; nothing in wmp.dll shows it.
    /// </summary>
    public uint[] PresentedPalette { get; } = new uint[256];

    public int PresetCount => _presets.Count;

    public int CurrentPreset => _current;

    public IReadOnlyList<BatteryPreset> Presets => _presets;

    public string PresetTitle(int index) => _presets[index].Title;

    /// <summary>The preset playing now.</summary>
    public BatteryPreset Preset => _presets[_current];

    /// <summary>
    /// Change the field size. The next frame goes through <see cref="AllocateSurfaces"/>, as the original's
    /// does after a resolution change: erased surfaces and SetSize on every preset. Each shift then rebuilds
    /// its table at the new size when it is next completed.
    /// </summary>
    public void Resize(int width, int height)
    {
        if (width == RenderData.W && height == RenderData.H) return;
        RenderData.Resize(width, height);
        _allocated = false;
    }

    /// <summary><c>SetCurrentPreset</c> (<c>0x18040e6a0</c>). No SetSize follows, so the new preset's
    /// height stays 0 and its shift countdown base is 60 until the next allocation.</summary>
    public void SetCurrentPreset(int index)
    {
        if (index < 0 || index >= _presets.Count || index == _current) return;
        _presets[_current].Free();
        _current = index;
        _presets[index].Allocate(Rand);
    }

    /// <summary>
    /// <c>CBattery::Render</c> (<c>0x18040e240</c>) minus the blit. Returns true when the field should be
    /// presented, and false when the original fills the rectangle with <see cref="StopFillColor"/>
    /// instead (stopped, after the fade).
    /// </summary>
    public bool Render(TimedLevelFrame frame)
    {
        _levels.LoadFrom(frame);
        return Render(_levels);
    }

    public bool Render(BatteryLevels levels)
    {
        if (!_allocated) AllocateSurfaces();
        DisplayPalette.CopyTo(PresentedPalette, 0); // GetDC

        switch (levels.State)
        {
            case TimedLevelFrame.StatePlaying:
                _fade = 300;
                if (RenderData.Palette.PerformTransition(Rand)) Upload();
                _presets[_current].Render(levels, Rand);
                return true;

            case TimedLevelFrame.StatePaused:
                return true;

            case TimedLevelFrame.StateStopped:
                if (_fade < 1) return false;
                var rd = RenderData;
                rd.Raster.Blur();
                _fade--;
                var c = rd.Canvas;
                c.Line(0, 0, rd.W - 1, 0, 1);
                c.Line(0, rd.H - 1, rd.W - 1, rd.H - 1, 1);
                c.Line(0, 0, 0, rd.H - 1, 1);
                c.Line(rd.W - 1, 0, rd.W - 1, rd.H - 1, 1);
                return true;

            default:
                return false;
        }
    }

    /// <summary>The stop-state fill: <c>CreateSolidBrush</c> of the CURRENT palette's entry 1.</summary>
    public uint StopFillColor => RenderData.Palette.Current[1] & 0x00FFFFFFu;

    /// <summary>The presented frame as BGRA (0xAARRGGBB ints): the field through
    /// <see cref="PresentedPalette"/>.</summary>
    public void CopyTo(int[] bgra)
    {
        var bits = RenderData.Front.Bits;
        Span<int> lut = stackalloc int[256];
        for (var i = 0; i < 256; i++) lut[i] = ToArgb(PresentedPalette[i]);
        for (var i = 0; i < bits.Length; i++) bgra[i] = lut[bits[i]];
    }

    /// <summary>PALETTEENTRY (R, G, B, flags) to 0xFFRRGGBB.</summary>
    public static int ToArgb(uint entry) =>
        unchecked((int)(0xFF000000u | (entry & 0xFF) << 16 | (entry & 0xFF00) | ((entry >> 16) & 0xFF)));

    /// <summary><c>OnFullscreenTransition</c> → <c>AllocateSurfaces</c> on the first frame:
    /// CreatePalette from the current palette, erased surfaces, and SetSize on every preset in order.</summary>
    private void AllocateSurfaces()
    {
        _allocated = true;
        RenderData.Palette.Current.CopyTo(DisplayPalette, 0);
        Array.Clear(RenderData.Front.Bits);
        Array.Clear(RenderData.Back.Bits);
        foreach (var p in _presets) p.SetSize(RenderData.W, RenderData.H);
    }

    private void Upload() => Array.Copy(RenderData.Palette.Current, DisplayPalette, 255);
}
