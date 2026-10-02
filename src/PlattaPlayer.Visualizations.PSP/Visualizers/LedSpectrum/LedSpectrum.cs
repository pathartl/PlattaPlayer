using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.PSP.Common;
using PlattaPlayer.Visualizations.PSP.Gu;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Visualizers;

/// <summary>
/// Visualizer type 2: LED spectrum analyzer (thumbnail music_tex_vis_thum_1). Ctor 0x2248, object size
/// 0x25d0, vtable 0x14748. Port of src/vis/led_spectrum.
///
/// <para><b>Grid:</b> 12 bands (columns) x 8 rows of 38x38-unit LED sprites, row 0 at the bottom (the
/// projection is y-up). Rows 0-3 use the blue textures, 4-6 yellow, 7 red (0x135dc = {4,3,1}). Each LED is
/// drawn twice: an always-faintly-visible "norml" sprite (alpha max(b, 32)) and an additive "light" sprite
/// (alpha min(b*b/255, 255)), with b = (uint8)(brightness * led). Vertex RGB stays white.</para>
///
/// <para><b>Audio (player states 0 and 1):</b> every frame the LED bytes decay by 0.85 and two dB trackers
/// per band fall (peak: velocity -= 0.01/frame, bar: -= 0.1/frame, both clamped at -40 dB). When a fresh
/// block arrives, the left channel (512 samples, Hann window) goes through FastDct(512); each band is the
/// max of |X[k]| * 2^-22 over DCT bins [start, end) (~130 Hz .. 11 kHz), converted to dB (-40 floor). A new
/// maximum makes a tracker jump to the top of its 5-dB LED step. Then the LEDs are re-lit from the trackers
/// with rand()&amp;15 flicker (the rand() call order is kept exactly, including the first, wasted, call).</para>
///
/// <para><b>Other states:</b> the trackers reset to -120 dB. State 2 scrolls a "fast forward" pictogram right,
/// state 3 a "rewind" pictogram left (one column every 10 frames); others freeze the LEDs.</para>
///
/// <para><b>Overlay:</b> a full-screen gradient tinted by the widget colour (0.3 / 0.4 / 0.5 / 0.65 of rgb at
/// the four corners), alpha = widget alpha * 0.7, drawn over the LEDs.</para>
///
/// <para><b>Port deviations:</b> the original verifies each RCO texture with SHA-1 of paf's internal surface
/// bytes against a table at 0x136c0 (digest[k] ^ name[k] == table[k*6 + variant*3 + color]). Our surfaces are
/// decoded GIMs, not paf's surface bytes, so that check could never pass; it is skipped and any non-null
/// texture is accepted (a missing texture still skips the rest of that colour, as in the original). The
/// plugin-View null check is dropped (the view is always present). SetOwner (0x2770) has no counterpart in
/// the managed base class. The unused pictogram table at 0x13738 (+0x25c0) is not carried over.</para>
/// </summary>
public sealed class LedSpectrum : Visualizer
{
    public const int Bands = 12;
    public const int Rows = 8;
    public const int FftSize = 512;

    /// <summary>vtype 0x19f = GU_TEXTURE_32BITF | GU_COLOR_8888 | GU_VERTEX_32BITF (0x18 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LedVertex
    {
        public float U, V;    // +0x00
        public uint Color;    // +0x08  0x00ffffff | alpha << 24
        public float X, Y, Z; // +0x0c
    }

    /// <summary>vtype 0x19c = GU_COLOR_8888 | GU_VERTEX_32BITF (0x10 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ColorVertex
    {
        public uint Color;    // +0x00
        public float X, Y, Z; // +0x04

        public ColorVertex(uint color, float x, float y, float z)
        {
            Color = color;
            X = x;
            Y = y;
            Z = z;
        }
    }

    // 0x136a8: RCO texture names, [color][variant]
    private static readonly string[,] TextureNames =
    {
        { "music_tex_led_blu_norml", "music_tex_led_blu_light" },
        { "music_tex_led_yel_norml", "music_tex_led_yel_light" },
        { "music_tex_led_red_norml", "music_tex_led_red_light" },
    };

    // 0x13310 / 0x13340: DCT bin range [start, end) of each band (512-point DCT, 44.1 kHz:
    // bin k ~ k * 43.07 Hz).
    private static readonly int[] BandStartTable = [3, 5, 7, 11, 15, 22, 31, 45, 63, 90, 127, 181];
    private static readonly int[] BandEndTable = [5, 7, 11, 15, 22, 31, 45, 63, 90, 127, 181, 256];

    // 0x135dc: LED rows per texture color (blue, yellow, red), bottom to top.
    private static readonly int[] RowsPerColor = [4, 3, 1];

    // 0x135e8: pattern scrolled while PlayerState == 2 ("fast forward" double arrow), [row][column].
    private static readonly byte[] PatternState2 =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 192, 0, 0, 192, 0, 0, 0, 0, 0,
        0, 0, 0, 192, 192, 0, 192, 192, 0, 0, 0, 0,
        0, 0, 0, 192, 192, 192, 192, 192, 192, 0, 0, 0,
        0, 0, 0, 192, 192, 0, 192, 192, 0, 0, 0, 0,
        0, 0, 0, 192, 0, 0, 192, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    // 0x13648: pattern scrolled while PlayerState == 3 ("rewind" double arrow).
    private static readonly byte[] PatternState3 =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 192, 0, 0, 192, 0, 0, 0,
        0, 0, 0, 0, 192, 192, 0, 192, 192, 0, 0, 0,
        0, 0, 0, 192, 192, 192, 192, 192, 192, 0, 0, 0,
        0, 0, 0, 0, 192, 192, 0, 192, 192, 0, 0, 0,
        0, 0, 0, 0, 0, 192, 0, 0, 192, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static readonly float WideHalfWidth = BitsFloat(0x438d2d2d); // 282.353 = 564.7059 / 2

    // base Visualizer fields: +0x04 alpha, +0x10 color[4], +0x20 owner, +0x24 brightness
    private readonly PafSurface?[,] _textures = new PafSurface?[3, 2];                // +0x28  [blu,yel,red][norml,light]
    private readonly LedVertex[] _normalLeds = new LedVertex[Rows * Bands * 2];      // +0x40   [8][12][2] sprites
    private readonly LedVertex[] _lightLeds = new LedVertex[Rows * Bands * 2];       // +0x1240
    private readonly ColorVertex[] _background = new ColorVertex[2];                 // +0x2440 black full-screen sprite
    private readonly ColorVertex[] _overlay = new ColorVertex[4];                    // +0x2460 tinted gradient (strip)
    private readonly byte[] _led = new byte[Rows * Bands];                           // +0x24a0 [8][12] brightness 0..255
    private readonly float[] _peakLevel = new float[Bands];                          // +0x2500 dB, slowly falling peak
    private readonly float[] _peakVelocity = new float[Bands];                       // +0x2530
    private readonly float[] _barLevel = new float[Bands];                           // +0x2560 dB, fast falling bar
    private readonly float[] _barVelocity = new float[Bands];                        // +0x2590

    // .bss globals in the original (0x15b20, 0x16320..0x1632c), shared by all instances; per instance here.
    private readonly float[] _spectrum = new float[FftSize]; // 0x15b20  DCT work buffer
    private int _state2Timer;                                // 0x16320
    private int _state2Offset;                               // 0x16324
    private int _state3Timer;                                // 0x16328
    private int _state3Offset;                               // 0x1632c

    /// <summary>0x2248</summary>
    public LedSpectrum(PspRuntime runtime) : base(runtime)
    {
        // (0x1031c SurfaceRCPtr init: the texture slots start null.)
        // The original returns here when paf's plugin View (DAT_00015b18) is null; it is always present.

        // Load the 6 LED textures. The SHA-1 integrity check is skipped (see the class summary).
        for (var color = 0; color < 3; ++color)
        {
            for (var variant = 0; variant < 2; ++variant)
            {
                var tex = Runtime.Assets.GetRcoTexture(TextureNames[color, variant]); // scePaf_23A245C3
                if (tex == null) break; // skips the rest of this color
                _textures[color, variant] = tex; // 0x10910 assign
            }
        }

        // LED sprites: 12 columns x 8 rows, 38x38 units, row 0 at the bottom (y up).
        var rowTop = 0xd1; // 209
        for (var row = 0; row < Rows; ++row)
        {
            var y0 = -((float)rowTop - 136.0f);
            var y1 = -(((float)rowTop + 38.0f) - 136.0f);
            var xs = -0x197;
            for (var col = 0; col < Bands; ++col)
            {
                var x = (float)(xs / 2 + 0xdd);
                xs += 0x4a;
                var x0 = x - 240.0f;
                var x1 = (x + 38.0f) - 240.0f;
                var i = (row * Bands + col) * 2;
                foreach (var v in new[] { _normalLeds, _lightLeds })
                {
                    v[i] = new LedVertex { U = 0.0f, V = 0.0f, Color = 0xffffffffu, X = x0, Y = y0, Z = 0.0f };
                    v[i + 1] = new LedVertex { U = 1.0f, V = 1.0f, Color = 0xffffffffu, X = x1, Y = y1, Z = 0.0f };
                }
            }
            rowTop -= 0x15;
        }

        // Full-screen quads (564.7 x 320 units, i.e. the 16:9 width, centered).
        _background[0] = new ColorVertex(0xff000000u, -WideHalfWidth, 160.0f, 0.0f);
        _background[1] = new ColorVertex(0xff000000u, WideHalfWidth, -160.0f, 0.0f);
        _overlay[0] = new ColorVertex(0, -WideHalfWidth, 160.0f, 0.0f);
        _overlay[1] = new ColorVertex(0, WideHalfWidth, 160.0f, 0.0f);
        _overlay[2] = new ColorVertex(0, -WideHalfWidth, -160.0f, 0.0f);
        _overlay[3] = new ColorVertex(0, WideHalfWidth, -160.0f, 0.0f);

        for (var i = 0; i < Bands; ++i)
        {
            _peakLevel[i] = -120.0f;
            _peakVelocity[i] = 0.0f;
            _barLevel[i] = -120.0f;
            _barVelocity[i] = 0.0f;
        }
        Array.Clear(_led);
    }

    /// <summary>0x2648: releases the textures (members destroyed in reverse order).</summary>
    public override void Dispose()
    {
        for (var i = 5; i >= 0; --i) _textures[i / 2, i % 2] = null;
        base.Dispose();
    }

    // part of 0x2778: decay, then (if a fresh block arrived) the spectrum analysis.
    private void Analyze(PcmBlock pcm)
    {
        var threshold = Pow10(-2.0f); // computed at the top of 0x2778

        for (var col = 0; col < Bands; ++col)
        {
            for (var row = 0; row < Rows; ++row)
            {
                var i = row * Bands + col;
                _led[i] = (byte)FloatToU32((float)_led[i] * 0.85f);
            }
            var peak = _peakLevel[col] + _peakVelocity[col];
            _barLevel[col] = _barLevel[col] + _barVelocity[col];
            _peakVelocity[col] = _peakVelocity[col] - 0.01f;
            _barVelocity[col] = _barVelocity[col] - 0.1f;
            _peakLevel[col] = peak;
            if (peak < -40.0f)
            {
                _peakLevel[col] = -40.0f;
                _peakVelocity[col] = 0.0f;
            }
            if (_barLevel[col] < -40.0f)
            {
                _barLevel[col] = -40.0f;
                _barVelocity[col] = 0.0f;
            }
        }

        if (pcm.Fresh == 0 || pcm.Bytes < 0x100) return;

        // Hann-windowed left channel, 512 samples (period 512: 0.5 - 0.5*cos(2*pi*i/512)).
        var samples = pcm.Samples;
        var spectrum = _spectrum;
        for (var i = 0; i < FftSize; ++i)
        {
            spectrum[i] = (float)samples[i * 2];
            var c = Cos((float)(i * 2) * BitsFloat(0x40490fdb) * 0.001953125f);
            spectrum[i] = spectrum[i] * (0.5f - c * 0.5f);
        }
        VisDsp.FastDct(spectrum, FftSize);
        for (var i = 0; i < FftSize; ++i)
        {
            var v = spectrum[i];
            if (!(0.0f < v)) v = -v;
            spectrum[i] = v * 2.3841858e-07f; // 2^-22
        }

        Span<float> band = stackalloc float[Bands];
        for (var b = 0; b < Bands; ++b)
        {
            band[b] = 0.0f;
            for (var k = BandStartTable[b]; k < BandEndTable[b]; ++k)
            {
                if (band[b] < spectrum[k]) band[b] = spectrum[k];
            }
        }

        for (var b = 0; b < Bands; ++b)
        {
            var db = -40.0f;
            if (threshold < band[b]) db = Log10(band[b]) * 20.0f;
            band[b] = db;
            // On a new maximum, jump to the top of the LED step containing it (steps of 5 dB).
            if (_peakLevel[b] < db)
            {
                _peakVelocity[b] = 0.0f;
                _peakLevel[b] = (float)((int)((db * 8.0f) / 40.0f) + 1) * 40.0f * 0.125f - 0.001f;
            }
            if (_barLevel[b] < db)
            {
                _barVelocity[b] = 0.0f;
                _barLevel[b] = (float)((int)((db * 8.0f) / 40.0f) + 1) * 40.0f * 0.125f - 0.001f;
            }
        }
    }

    // part of 0x2778: relight the LEDs from the levels (with random flicker).
    private void LightLeds()
    {
        for (var col = 0; col < Bands; ++col)
        {
            var r = Runtime.Rand();
            // This value is always overwritten below, but the rand() call must stay.
            _led[col] = (byte)((r & 0x3f) + 0x90);
            var barRows = (int)(((_barLevel[col] + 40.0f) / 40.0f) * 8.0f);
            var peakRow = (int)(((_peakLevel[col] + 40.0f) / 40.0f) * 8.0f);
            if (barRows < 0) barRows = 0;
            if (!(barRows < 8)) barRows = 7;
            var rows = (float)barRows;
            if (peakRow < 0) peakRow = 0;
            var barValue = (byte)FloatToU32((1.0f - rows * 0.25f * 0.125f) * 208.0f);
            if (!(peakRow < 8)) peakRow = 7;
            var peakValue = (byte)FloatToU32((1.0f - rows * 0.2f * 0.125f) * 224.0f);
            var baseValue = (byte)FloatToU32((1.0f - rows * 0.2f * 0.125f) * 240.0f);
            for (var row = 1; row < Rows; ++row)
            {
                if (row <= barRows)
                    _led[row * Bands + col] = (byte)(barValue + (Runtime.Rand() & 0xf));
            }
            _led[peakRow * Bands + col] = (byte)(peakValue + (Runtime.Rand() & 0xf));
            _led[col] = (byte)(baseValue + (Runtime.Rand() & 0xf));
        }
    }

    /// <summary>0x2778</summary>
    public override void Render(PcmBlock pcm)
    {
        var state = Runtime.PlayerState;
        if (state == 0 || state == 1)
        {
            Analyze(pcm);
            LightLeds();
        }
        else
        {
            for (var i = 0; i < Bands; ++i)
            {
                _peakLevel[i] = -120.0f;
                _peakVelocity[i] = 0.0f;
                _barLevel[i] = -120.0f;
                _barVelocity[i] = 0.0f;
            }
            if (state == 2)
            {
                // scroll the "fast forward" pictogram to the right, one column per 10 frames
                _state2Timer = _state2Timer + 1;
                if (!(_state2Timer < 10))
                {
                    _state2Timer = 0;
                    _state2Offset = (_state2Offset + 0xb) % 0xc;
                }
                for (var col = 0; col < Bands; ++col)
                {
                    var src = (col + _state2Offset) % 0xc;
                    for (var row = 0; row < Rows; ++row) _led[row * Bands + col] = PatternState2[row * Bands + src];
                }
            }
            else if (state == 3)
            {
                // scroll the "rewind" pictogram to the left
                _state3Timer = _state3Timer + 1;
                if (!(_state3Timer < 10))
                {
                    _state3Timer = 0;
                    _state3Offset = (_state3Offset + 1) % 0xc;
                }
                for (var col = 0; col < Bands; ++col)
                {
                    var src = (col + _state3Offset) % 0xc;
                    for (var row = 0; row < Rows; ++row) _led[row * Bands + col] = PatternState3[row * Bands + src];
                }
            }
            // other states: LEDs stay frozen
        }

        // Per-LED vertex alpha.
        var brightness = Brightness;
        for (var i = 0; i < Rows * Bands; ++i)
        {
            var a = FloatToU32(brightness * (float)_led[i]) & 0xff;
            var sq = (int)(a * a);
            if (a < 0x20) a = 0x20; // unlit LEDs stay faintly visible
            var lightA = sq / 0xff;
            if (lightA > 0xff) lightA = 0xff;
            ref var l0 = ref _lightLeds[i * 2];
            ref var l1 = ref _lightLeds[i * 2 + 1];
            ref var n0 = ref _normalLeds[i * 2];
            ref var n1 = ref _normalLeds[i * 2 + 1];
            l0.Color = (l0.Color & 0xffffff) | (uint)lightA << 24;
            l1.Color = (l1.Color & 0xffffff) | (uint)lightA << 24;
            n0.Color = (n0.Color & 0xffffff) | a << 24;
            n1.Color = (n1.Color & 0xffffff) | a << 24;
        }

        // Overlay gradient, tinted by the widget color: 0.3 top-left, 0.4 top-right,
        // 0.5 bottom-left, 0.65 bottom-right; alpha = widget alpha * 0.7.
        float g = Color[1], r = Color[0], b = Color[2];
        var alpha = (uint)((int)((Alpha * 0.7f + 0.0f) * 255.0f) << 24);
        uint Shade(float k) =>
            ((uint)(int)((r * k + 0.0f) * 255.0f) & 0xff) |
            ((uint)(int)((g * k + 0.0f) * 255.0f) & 0xff) << 8 |
            ((uint)(int)((b * k + 0.0f) * 255.0f) & 0xff) << 16;
        _overlay[3].Color = Shade(0.65f) | alpha;
        _background[0].Color = 0xff000000u;
        _overlay[0].Color = Shade(0.3f) | alpha;
        _overlay[1].Color = Shade(0.4f) | alpha;
        _overlay[2].Color = Shade(0.5f) | alpha;
        _background[1].Color = 0xff000000u;

        Gu.Disable(GU_LIGHTING);
        Gu.Enable(GU_BLEND);
        Gu.BlendFunc(GU_ADD, GU_SRC_ALPHA, GU_ONE_MINUS_SRC_ALPHA, 0, 0);
        // (sceKernelDcacheWritebackAll: not needed here)
        Gu.Disable(GU_TEXTURE_2D);
        Gu.GumDrawArray<ColorVertex>(GU_SPRITES, GU_COLOR_8888 | GU_VERTEX_32BITF, 2, _background);
        // additive: src * srcAlpha + dst * 0xffffff
        Gu.BlendFunc(GU_ADD, GU_SRC_ALPHA, GU_FIX, 0, 0xffffffffu);
        Gu.Enable(GU_TEXTURE_2D);
        Gu.TexFunc(GU_TFX_MODULATE, GU_TCC_RGBA);
        if (_textures[0, 0] != null)
        {
            var normal = 0;
            var light = 0;
            for (var c = 0; c < 3; ++c)
            {
                var rows = RowsPerColor[c];
                if (rows > 0)
                {
                    var count = rows * 0x18;
                    // The LEDs use sceGuDrawArray (no matrix upload), as the binary does.
                    Gu.SetTexture(_textures[c, 0], 0.0f, 0.0f, 1.0f, 1.0f);
                    Gu.DrawArray<LedVertex>(GU_SPRITES, GU_TEXTURE_32BITF | GU_COLOR_8888 | GU_VERTEX_32BITF, count,
                        _normalLeds.AsSpan(normal, count));
                    Gu.SetTexture(_textures[c, 1], 0.0f, 0.0f, 1.0f, 1.0f);
                    Gu.DrawArray<LedVertex>(GU_SPRITES, GU_TEXTURE_32BITF | GU_COLOR_8888 | GU_VERTEX_32BITF, count,
                        _lightLeds.AsSpan(light, count));
                    light += count;
                    normal += count;
                }
            }
        }
        Gu.BlendFunc(GU_ADD, GU_SRC_ALPHA, GU_FIX, 0, 0xffffffffu);
        Gu.Disable(GU_TEXTURE_2D);
        Gu.GumDrawArray<ColorVertex>(GU_TRIANGLE_STRIP, GU_COLOR_8888 | GU_VERTEX_32BITF, 4, _overlay);
    }
}
