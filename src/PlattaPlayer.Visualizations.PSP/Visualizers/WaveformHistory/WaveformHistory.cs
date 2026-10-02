using PlattaPlayer.Visualizations.PSP.Common;
using PlattaPlayer.Visualizations.PSP.Gu;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Visualizers;

/// <summary>
/// Visualizer type 4: "waveform history" (thumbnail music_tex_vis_thum_3: glowing wave lines). Ctor 0x6a20,
/// object size 0xc0, vtable 0x14838. Port of src/vis/waveform_history.
///
/// <para><b>Audio:</b> every second frame (frame counter incremented first, update when odd) the first 50
/// frames of the PCM block, left channel only, are Hann-windowed (<c>s * (0.5 - Cos(i*2pi/50)*0.5)</c>,
/// truncated to int16). No FFT or smoothing. Player state 1 (no audio) gives a flat line.</para>
///
/// <para><b>Mapping:</b> y = ((s*200 + 0x640000) &gt;&gt; 16) + 50 (about 50..250); x = 0, W/50 ... 48W/50,
/// then W for the last point. W is 480, or 564.7059 on a 16:9 output.</para>
///
/// <para><b>History:</b> 32 ribbons in a ring, redrawn every frame oldest first with GU_MAX blending. Age k
/// (1..31) uses f = (32-k)/32: colour = (rgb/3)*f, alpha = brightness*(uint8)(f*40). The newest ribbon is
/// drawn last with full rgb and alpha = brightness*255.</para>
///
/// <para><b>Colour:</b> the widget colour through RgbToHsv, V replaced by 220, hue swept slowly with
/// <c>Sin(frame*0.0005 + Asin((hue-180)/180))*179 + 180</c>, back through HsvToRgb, times brightness.</para>
///
/// <para><b>Glow texture (0xe440):</b> 32x32 RGBA8888 generated at load time in the PSP <i>swizzled</i>
/// layout and bound with TexMode(8888, 0, 0, swizzle=1) (GuContext unswizzles). The profile depends only on
/// the column: |d|&lt;=4 white, |d|=5 0xffffffbf (a seb sign-extension quirk of the original, giving the edge a
/// cyan tint), |d|=6 0x7f7f7f7f, |d|=7 0x3f3f3f3f, else 0. TexFunc is not set: it inherits the previous
/// state.</para>
/// </summary>
public sealed class WaveformHistory : Visualizer
{
    public const int History = 32;  // ring of ribbons
    public const int Points = 50;   // waveform points per ribbon
    public const int GlowSize = 32; // glow texture is 32x32 RGBA8888, swizzled

    // base Visualizer fields: +0x04 alpha, +0x10 color[4], +0x20 owner, +0x24 brightness
    private readonly GlowVertex[] _vertexPool;                         // +0x28  memalign(0x40, 32 * BufferBytes(51))
    private uint _head;                                                // +0x2c  ring index of the newest ribbon (0..31)
    private readonly GlowLineStrip[] _strips = new GlowLineStrip[History]; // +0x30..+0xac
    private PafSurface? _glowSurface;                                  // +0xb0  SurfaceRCPtr
    private uint[]? _glowPixels;                                       // +0xb4  locked pixels (non-null = loaded)
    private uint _frame;                                               // +0xb8  frame counter

    /// <summary>0x6a20</summary>
    public WaveformHistory(PspRuntime runtime) : base(runtime)
    {
        _head = 0;
        _glowSurface = null;
        _glowPixels = null;
        _frame = 0;
        var bytes = GlowLineStrip.BufferBytes(0x33);
        const int vertexBytes = 0x14;
        _vertexPool = new GlowVertex[(bytes << 5) / vertexBytes]; // sce_paf_private_memalign
        var offset = 0;
        for (var i = 0; i < History; ++i)
        {
            // note: the strip only clears BufferBytes(50) of its BufferBytes(51) slice
            _strips[i] = new GlowLineStrip(0x32, _vertexPool, offset / vertexBytes);
            offset += bytes;
        }
    }

    /// <summary>0x6aec</summary>
    public override void Dispose()
    {
        // (strips and the vertex pool are garbage collected)
        UnloadResources(); // direct (non-virtual) call in the original
        _glowSurface = null; // SurfaceRCPtr dtor
        base.Dispose();
    }

    /// <summary>0x6c7c</summary>
    public override void UnloadResources()
    {
        if (IsLoaded())
        {
            _glowPixels = null;
            _glowSurface!.Unlock();
            _glowSurface = null; // 0x10910 assign(nullptr)
        }
    }

    /// <summary>0x6cd8</summary>
    public override void LoadResources()
    {
        if (IsLoaded()) return;
        // paf_Surface_ctor(s, 32, 32, mode 3 (8888), 0, 0, 1, 0, 0)
        var s = new PafSurface(0x20, 0x20, PafSurface.Mode8888);
        _glowSurface = s; // 0x10910 assign
        _glowPixels = _glowSurface.Lock(0);
        GenerateGlowTexture(6.5f, _glowPixels, 0x20);
        // The surface stays locked while loaded; the pixels are used directly as GU texture.
    }

    /// <summary>0x6d8c</summary>
    public override bool IsLoaded() => _glowPixels != null;

    /// <summary>
    /// 0xe440: fills a size x size RGBA8888 texture in PSP swizzled layout (16-byte x 8-row blocks) with a
    /// horizontal glow profile. <paramref name="unused"/> arrives in $f12 (6.5) but is never read.
    /// </summary>
    public static void GenerateGlowTexture(float unused, uint[] pixels, int size)
    {
        _ = unused;
        var half = size / 2;
        var blocksPerRow = (size * 4) / 16; // 16-byte swizzle blocks per row
        for (var row = 0; row < size; ++row)
        {
            var rowOffset = ((row / 8) * blocksPerRow * 8 + (row & 7)) * 16;
            for (var col = 0; col < size; ++col)
            {
                var xbytes = (col * 32) / 8; // = col * 4
                var d = col - half;
                var value = 0xffffffffu; // |d| <= 3: opaque white
                if (!((uint)(d + 3) < 7))
                {
                    var ad = d < -d ? -d : d; // Allegrex max(d, -d)
                    var t = (8 - ad) * 0xff;
                    value = 0;
                    if ((uint)(d + 7) < 0xf)
                    {
                        // seb: sign-extended byte, so |d| = 4 (255) and 5 (191) become 0xffffffxx
                        value = (uint)(int)(sbyte)(t / 4);
                    }
                }
                var pixel = value << 24 | value << 16 | value << 8 | value;
                var byteOffset = rowOffset + (xbytes / 16) * 0x80 + (xbytes & 0xf);
                pixels[byteOffset / 4] = pixel;
            }
        }
    }

    // Waveform sample -> ribbon y: (s*200 + 0x640000) >> 16, + 50  (arithmetic shift).
    private static float SampleToY(short s) => (float)(((s * 200 + 0x640000) >> 16) + 0x32);

    /// <summary>0x6d98</summary>
    public override void Render(PcmBlock pcm)
    {
        var samples = pcm.Samples;
        _frame = _frame + 1;
        var width = 480.0f;
        if (Runtime.IsWideOutput) width = BitsFloat(0x440d2d2d); // 564.7059

        if ((_frame & 1) != 0)
        {
            // Stack buffer: one int16 per 4-byte slot (only the low half is used).
            Span<short> wave = stackalloc short[Points * 2];
            _head = (_head + 1) & 0x1f;
            if (Runtime.PlayerState == 1)
            {
                wave.Clear(); // sceKernelMemset(wave, 0, 200)
            }
            else
            {
                for (var i = 0; i < Points; ++i)
                {
                    var s = samples[i * 2]; // left channel of frame i
                    var w = Cos(((float)i * BitsFloat(0x40c90fdb)) / 50.0f); // 2*pi*i/50
                    wave[i * 2] = (short)(int)((float)s * (0.5f - w * 0.5f)); // Hann window
                }
            }
            var strip = _strips[_head];
            strip.Begin(0.0f, SampleToY(wave[0]), 0.0f, width / 50.0f, SampleToY(wave[2]), 0.5f);
            for (var i = 2; i < 0x31; ++i)
            {
                _strips[_head].AddPoint(((float)i * width) / 50.0f, SampleToY(wave[i * 2]), 0.5f);
            }
            _strips[_head].End(width, SampleToY(wave[0x31 * 2]), 0.5f);
        }

        Gu.GumPushMatrix();
        Gu.GumTranslate(-width * 0.5f, -162.0f, 0.0f);
        Gu.Disable(GU_BLEND);
        Gu.Disable(GU_TEXTURE_2D);
        Gu.ClearColor(0);
        Gu.Clear(GU_COLOR_BUFFER_BIT | GU_STENCIL_BUFFER_BIT | GU_FAST_CLEAR_BIT); // 0x13
        Gu.Enable(GU_TEXTURE_2D);
        Gu.Disable(GU_LIGHTING);
        Gu.BlendFunc(GU_MAX, 0, 0, 0, 0);
        Gu.Enable(GU_BLEND);
        Gu.TexOffset(0.0f, 0.0f);
        Gu.TexScale(1.0f, 1.0f);
        Gu.TexImage(0, 0x20, 0x20, 0x20, _glowPixels!);
        Gu.TexMode(GU_PSM_8888, 0, 0, 1); // swizzled
        // (sceKernelDcacheWritebackAll: not needed here)

        // Ribbon color: hue taken from the widget color, swept slowly with a sine; V fixed at 220.
        var brightness = Brightness;
        var r = (byte)FloatToU32(Color[0] * 255.0f);
        var g = (byte)FloatToU32(Color[1] * 255.0f);
        var b = (byte)FloatToU32(Color[2] * 255.0f);
        VisColor.RgbToHsv(r, g, b, out var hue, out var sat, out var val);
        val = 0xdc;
        var t = (float)(int)_frame * 0.0005f;
        var phase = Asin((float)(hue - 0xb4) / 180.0f);
        hue = (int)(Sin(t + phase) * 179.0f + 180.0f);
        var rgb = VisColor.HsvToRgb(hue, sat, val);

        var cr = FloatToU32((float)(rgb & 0xff) * brightness) & 0xff;
        var cg = FloatToU32((float)(rgb >> 8 & 0xff) * brightness) & 0xff;
        var cb = FloatToU32((float)(rgb >> 16 & 0xff) * brightness) & 0xff;
        // trails use a third of the color
        var trailB = (float)(int)(cb / 3);
        var trailR = (float)(int)(cr / 3);
        var trailG = (float)(int)(cg / 3);
        var headColor = (cb << 16 | cg << 8 | cr) | (uint)((int)(brightness * 255.0f) << 24);

        Gu.AlphaFunc(GU_NOTEQUAL, 0, 0xff);
        Gu.Enable(GU_ALPHA_TEST);
        for (var age = 0x1f; age > 0; --age) // oldest first
        {
            var index = (_head - (uint)age) & 0x1f;
            var f = (float)(0x20 - age) * 0.03125f;
            var a = FloatToU32(f * 40.0f) & 0xff;
            var tr = FloatToU32(trailR * f) & 0xff;
            var tg = FloatToU32(trailG * f) & 0xff;
            var tb = FloatToU32(trailB * f) & 0xff;
            var c = (tb << 16 | tg << 8) | tr;
            Gu.Color(c | (uint)((int)(brightness * (float)a) << 24));
            _strips[index].Draw(Gu);
        }
        Gu.Color(headColor);
        _strips[_head].Draw(Gu);
        Gu.Disable(GU_ALPHA_TEST);
        Gu.Enable(GU_BLEND);
        Gu.GumPopMatrix();
    }
}
