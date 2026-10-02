using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.PSP.Common;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Visualizers;

/// <summary>
/// Visualizer type 5: "block wave" (thumbnail music_tex_vis_thum_4: pink/red block grid). Ctor 0x7628,
/// object size 0xb790, vtable 0x14878. Port of src/vis/block_wave.
///
/// <para><b>Grid:</b> Render calls SetBlockSize(20) every frame: 26 columns (480/20+2), 5 levels
/// ((272/20)/3+1), 52 sprite vertices per row.</para>
///
/// <para><b>Audio:</b> every second frame (when the incremented frame counter is odd) column n becomes the
/// signed-integer mean of int16 samples [n*8, n*8+8) (4 stereo frames, L and R mixed), quantised to
/// <c>sign(s)*((5*|s|)/65536) + 2</c> (0..4). Player state 1 gives a flat line at level 2. A
/// LevelMeter(-40 dB, 0.3, mono) is updated on each update frame but never read.</para>
///
/// <para><b>Rows:</b> column n is a 20x20 sprite (inset 1) at level*20 + 60, displaced by a swirl
/// 40*shake^3*(cos, sin)(n*3pi/26) that is only nonzero after a track change. A ring of 32 rows is drawn
/// oldest first after Translate(-240, -156, 0); row of age a uses f = ((32-a)/32)^2 * k with
/// k = 2b - b^2 (b = brightness): colour = rgb*f + grey*(1-f) (a manual fade toward the background), alpha
/// byte f*255 (blending is off, so it has no effect).</para>
///
/// <para><b>Track change</b> (OnPlayerEvent type 7): a 50-frame shake; the background grey flips randomly
/// between 0 and 0xcc (cross-faded, scaled by k) and the drawn part of every row shrinks to nothing and
/// grows back. Colours: hue swept like type 4 (<c>Sin(frame*0.0005 + Asin((h-180)/180))*179+180</c>, frame
/// before the increment, unsigned), S and V from the widget colour, scaled by brightness.</para>
/// </summary>
public sealed class BlockWave : Visualizer
{
    public const int History = 32;
    public const int MaxColumns = 61; // 0x5b8 bytes per row / 0x18 per sprite

    /// <summary>vtype 0x180 = GU_VERTEX_32BITF, 0x0c bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex
    {
        public float X, Y, Z;

        public Vertex(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    private const int RowVertices = MaxColumns * 2;

    // base Visualizer fields: +0x04 alpha, +0x10 color[4], +0x20 owner, +0x24 brightness
    private int _ctorBlockSize;    // +0x28  8, set by the ctor, never read
    private int _blockSize;        // +0x2c  (uninitialised until the first Render)
    private int _columns;          // +0x30  480 / size + 2
    private int _levels;           // +0x34  (272 / size) / 3 + 1
    private int _lineVertexCount;  // +0x38  2 * columns
    private readonly Vertex[] _lines = new Vertex[History * RowVertices]; // +0x3c  [32][61][2], 0x5b8 bytes per row
    private uint _head;            // +0xb73c ring index of the newest row
    private uint _frame;           // +0xb740
    private int _shakeTimer;       // +0xb744 50 -> 0 frames after a track change
    private byte _previousGrey;    // +0xb748 background grey before the last change
    private byte _grey;            // +0xb749 background grey: 0 or 0xcc
    private readonly Vertex[] _background = new Vertex[2]; // +0xb74c full-screen sprite
    private readonly LevelMeter _meter;                     // +0xb764 updated but never read

    /// <summary>0x7628</summary>
    public BlockWave(PspRuntime runtime) : base(runtime)
    {
        _meter = new LevelMeter(-40.0f, BitsFloat(0x3e99999a) /*0.3*/, true);
        _grey = 0xcc;
        _previousGrey = 0xcc;
        _head = 0;
        _frame = 0;
        _shakeTimer = 0;
        Array.Clear(_lines); // sceKernelMemset(lines, 0, 0xb700)
        // An older inlined version of SetBlockSize(8): note it stores to +0x28 (not +0x2c)
        // and levels = 0x11. All of these are overwritten by Render's SetBlockSize(20).
        _ctorBlockSize = 8;
        _columns = 0x3e;
        _blockSize = 0; // not initialised in the original
        _background[0] = new Vertex(BitsFloat(0xc38d2d2d), -160.0f, 0.0f); // -282.353
        _lineVertexCount = 0x7c;
        _background[1] = new Vertex(BitsFloat(0x438d2d2d), 160.0f, 0.0f);  //  282.353
        _levels = 0x11;
        _ = _ctorBlockSize;
    }

    /// <summary>0x77a8: derive the grid from the block size (Render calls it with 20 every frame).</summary>
    private void SetBlockSize(int size)
    {
        _blockSize = size;
        _columns = 0x1e0 / size + 2;
        _lineVertexCount = _columns * 2;
        _levels = (0x110 / size) / 3 + 1;
    }

    /// <summary>0x75e4: quantise sample s to 0..levels-1 (around levels/2).</summary>
    private static int QuantizeSample(int s, int levels)
    {
        var a = s < -s ? -s : s;       // max(s, -s)
        var v = (levels * a) / 0x10000; // signed, rounds toward zero
        if (s < 0) v = -v;
        return v + levels / 2;
    }

    /// <summary>0x813c</summary>
    public override void OnPlayerEvent(PlayerEvent ev)
    {
        if (ev.Type == 7) // track change
        {
            _shakeTimer = 0x32;
            _previousGrey = _grey;
            if ((Runtime.Rand() & 1) == 0)
            {
                _grey = 0;
            }
            else
            {
                _grey = 0xcc;
            }
        }
    }

    /// <summary>0x7800</summary>
    public override void Render(PcmBlock pcm)
    {
        var samples = pcm.Samples;
        var timer = _shakeTimer;
        if (timer > 0)
        {
            timer = timer - 1;
            _shakeTimer = timer;
        }
        var brightness = Brightness;
        var shake = (float)timer * 0.02f;
        shake = shake * (shake * shake); // (timer/50)^3
        SetBlockSize(0x14);

        // Block color: hue from the widget color, swept slowly with a sine; S kept, V from the color.
        var r = (byte)FloatToU32(Color[0] * 255.0f);
        var g = (byte)FloatToU32(Color[1] * 255.0f);
        var b = (byte)FloatToU32(Color[2] * 255.0f);
        VisColor.RgbToHsv(r, g, b, out var hue, out var sat, out var val);
        var t = (float)_frame * 0.0005f; // unsigned -> float
        var phase = Asin((float)(hue - 0xb4) / 180.0f);
        hue = (int)(Sin(t + phase) * 179.0f + 180.0f);
        var rgb = VisColor.HsvToRgb(hue, sat, val);
        var cr = FloatToU32((float)(rgb & 0xff) * brightness) & 0xff;
        var cg = FloatToU32((float)(rgb >> 8 & 0xff) * brightness) & 0xff;
        var cb = FloatToU32((float)(rgb >> 16 & 0xff) * brightness) & 0xff;

        _frame = _frame + 1;
        if ((_frame & 1) != 0)
        {
            _meter.Update(pcm); // result unused
            _head = (_head + 1) & 0x1f;
            Span<short> wave = stackalloc short[0x7a];
            int columns;
            if (Runtime.PlayerState == 1)
            {
                wave.Clear(); // sceKernelMemset(wave, 0, 0xf4)
                columns = _columns;
            }
            else
            {
                columns = _columns;
                // each column: mean of 8 consecutive int16 (4 stereo frames, L and R mixed)
                for (var n = 0; n < columns; ++n)
                {
                    var p = n * 8;
                    var sum = 0;
                    for (var k = 0; k < 8; ++k) sum += samples[p + k];
                    wave[n] = (short)(sum / 8);
                }
            }
            var inset = (float)(_blockSize / 2 - 2);
            var amplitude = shake * 40.0f;
            if (1.0f < inset) inset = 1.0f;
            for (var n = 0; n < columns; ++n)
            {
                var angle = ((float)n * BitsFloat(0x4116cbe4)) / (float)columns; // n * 3pi / columns
                var dx = amplitude * Cos(angle);
                var dy = amplitude * Sin(angle);
                var level = QuantizeSample(wave[n], _levels);
                var size = _blockSize;
                var i = (int)_head * RowVertices + n * 2;
                ref var v0 = ref _lines[i];
                ref var v1 = ref _lines[i + 1];
                var y = level * size;
                v0.Y = (float)(y + 0x3c) + inset + dy;
                v0.Z = 0.0f;
                v0.X = (float)((n - 1) * size) + inset + dx;
                v1.Y = ((float)(y + size + 0x3c) - inset) + dy;
                v1.X = ((float)(n * size) - inset) + dx;
                v1.Z = 0.0f;
            }
        }

        Gu.Disable(GU_BLEND);
        Gu.Disable(GU_TEXTURE_2D);
        Gu.GumPushMatrix();
        var bgLevel = FloatToU32(shake * (float)_previousGrey + (1.0f - shake) * (float)_grey) & 0xff;
        var kb = (brightness + brightness) - brightness * brightness; // 1 - (1 - brightness)^2
        var grey = FloatToU32((float)bgLevel * kb) & 0xff;
        // (sceKernelDcacheWritebackAll: not needed here)
        Gu.Color(grey << 16 | grey << 8 | grey);
        Gu.GumDrawArray<Vertex>(GU_SPRITES, GU_VERTEX_32BITF, 2, _background);
        Gu.GumTranslate(-240.0f, -156.0f, 0.0f);

        var fr = (float)(int)cr;
        var fgrey = (float)(int)grey;
        var fg = (float)(int)cg;
        var fb = (float)(int)cb;
        // the visible part of every row shrinks while shaking
        var count = (int)((float)(_lineVertexCount / 2) * (1.0f - shake)) << 1;
        if (count < 0) count = 2;
        for (var age = 0x1f; age >= 0; --age) // oldest first, newest (age 0) last
        {
            var index = (_head - (uint)age) & 0x1f;
            var f = (float)(0x20 - age) * 0.03125f;
            f = f * f;
            f = f * kb;
            var a = FloatToU32(f * 255.0f) & 0xff;
            var inv = 1.0f - f;
            var lr = FloatToU32(fr * f + inv * fgrey) & 0xff;
            var lg = FloatToU32(fg * f + inv * fgrey) & 0xff;
            var lb = FloatToU32(fb * f + inv * fgrey) & 0xff;
            Gu.Color((lb << 16 | lg << 8) | lr | a << 24);
            Gu.GumDrawArray<Vertex>(GU_SPRITES, GU_VERTEX_32BITF, count,
                _lines.AsSpan((int)index * RowVertices, count));
        }
        Gu.GumPopMatrix();
        Gu.Enable(GU_BLEND);
        Gu.Enable(GU_TEXTURE_2D);
    }
}
