using PlattaPlayer.Visualizations.PSP.Common;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;

// Helper classes of visualizer type 7 ("sand trails", src/vis/sand_trails/sand_canvas.h/.cpp): the
// software-rendered canvas, the wandering trail walkers, the colour schemes and a few small math helpers.
// All of these live only in the render thread of type 7 (0x89a8) or in its ctor (0x8238).
// Every function was checked against the disassembly; float operation order follows the MIPS code.
namespace PlattaPlayer.Visualizations.PSP.Visualizers.Sand;

/// <summary>Local math of type 7: polynomial sine/cosine (distinct from the shared VisMath Sin/Cos) and
/// the Gaussian table builders.</summary>
internal static class SandMath
{
    private static readonly float TwoPi = BitsFloat(0x40c90fdb);  // 6.2831855
    private static readonly float Pi = BitsFloat(0x40490fdb);     // 3.1415927
    private static readonly float HalfPi = BitsFloat(0x3fc90fdb); // 1.5707964

    /// <summary>0x81a4: odd Taylor-like polynomial, |x| &lt;= pi/2.</summary>
    public static float PolySinCore(float x)
    {
        var x2 = x * x;
        return ((x2 * BitsFloat(0xb94b9f27) + BitsFloat(0x3c08801c)) * x2 + BitsFloat(0xbe2aaa98)) * x2 * x + x;
    }

    /// <summary>0x81ec: even polynomial, |x| &lt;= pi/2.</summary>
    public static float PolyCosCore(float x)
    {
        x = x * x;
        return ((x * BitsFloat(0xbab3d431) + BitsFloat(0x3d2aa6fb)) * x + BitsFloat(0xbefffff9)) * x + 1.0f;
    }

    /// <summary>0x95d8: range reduction + PolySinCore/PolyCosCore.</summary>
    public static float PolySin(float x)
    {
        var sign = 1;
        if (x < 0.0f)
        {
            x = -x;
            sign = -1;
        }
        if (TwoPi < x) x = x - (float)(int)(x / TwoPi) * TwoPi;
        if (Pi < x)
        {
            x = x - Pi;
            sign = -sign;
        }
        if (HalfPi < x) x = Pi - x;
        float r;
        if (BitsFloat(0x3f6ad720) < x) // 0.917345
            r = PolyCosCore(HalfPi - x);
        else
            r = PolySinCore(x);
        return r * (float)sign;
    }

    /// <summary>0x96bc: range reduction + PolyCosCore/PolySinCore.</summary>
    public static float PolyCos(float x)
    {
        var sign = 1;
        if (x < 0.0f) x = -x;
        if (TwoPi < x) x = x - (float)(int)(x / TwoPi) * TwoPi;
        if (Pi < x)
        {
            x = x - Pi;
            sign = -1;
        }
        if (HalfPi < x)
        {
            x = Pi - x;
            sign = -sign;
        }
        float r;
        if (BitsFloat(0x3f274895) < x) // 0.653451
            r = PolySinCore(HalfPi - x);
        else
            r = PolyCosCore(x);
        return r * (float)sign;
    }

    /// <summary>0x122d8: w x h bytes, value = (uint8)(Exp(-(dx^2+dy^2) / (2 sigma^2)) * 145).</summary>
    public static void BuildGaussianBytes(float sigma, byte[] output, int w, int h)
    {
        var coef = 1.0f / ((sigma + sigma) * sigma);
        var o = 0;
        for (var row = 0; row < h; row++)
        {
            var dy = row - h / 2;
            for (var col = 0; col < w; col++)
            {
                var dx = col - w / 2;
                var v = Exp(-(float)(dx * dx + dy * dy) * coef);
                v = v * 145.0f;
                output[o++] = (byte)FloatToU32(v);
            }
        }
    }

    /// <summary>0x12448: w x h floats exp(-(dx^2+dy^2) * coef), normalised so the sum is 2^20 and truncated
    /// to integers (stored as float).</summary>
    public static void BuildGaussianKernel(float coef, Span<float> output, int w, int h)
    {
        var sum = 0.0f;
        for (var row = 0; row < h; row++)
        {
            var dy = row - h / 2;
            for (var col = 0; col < w; col++)
            {
                var dx = col - w / 2;
                var v = Exp(-(float)(dx * dx + dy * dy) * coef);
                output[row * w + col] = v;
                sum = sum + v;
            }
        }
        for (var i = 0; i < w * h; i++) output[i] = (float)(int)((output[i] / sum) * 1048576.0f);
    }
}

/// <summary>
/// Canvas description (vtable 0x14948, dtor 0x113bc / 0x113cc). Built on the render thread's stack and
/// copied into the <see cref="Canvas"/>. The original points into one 0xbf400-byte work buffer (height
/// map +0, backup +0x1fe00, RGBA image +0x3fc00); the port uses three typed arrays.
/// </summary>
internal struct CanvasDesc
{
    // +0x00 vtable 0x14948
    public byte[] HeightMap; // +0x04  w*h byte height map (work buffer + 0)
    public uint[] Rgba;      // +0x08  w*h RGBA8888 output image (work buffer + 0x3fc00)
    public byte[] Backup;    // +0x0c  w*h copy of the initial height map (work buffer + 0x1fe00)
    public int Width;        // +0x10  480
    public int Height;       // +0x14  272
    public int BytesPerPixel; // +0x18 1
}

/// <summary>
/// The software canvas (vtable 0x14938; ctor 0x113f4, dtor 0x11568 / 0x11584; 0x4114 bytes, on the
/// render thread's stack). Holds the height map description and the lighting parameters used when height
/// values are turned into colours through the 256-entry palette.
/// </summary>
internal sealed class Canvas
{
    // +0x00 vtable 0x14938, +0x04 not initialised / unused
    public readonly byte[] GaussBytes = new byte[11 * 11];      // +0x08  BuildGaussianBytes(0.19) - never read afterwards
    public readonly float[] Kernels = new float[34 * 11 * 11];  // +0x84  34 x BuildGaussianKernel  - never read afterwards
    public CanvasDesc Desc;                                     // +0x40cc (vtable) .. +0x40e4
    public int Unused40e8 = 0;                                    // +0x40e8
    public int Unused40ec = 0;                                    // +0x40ec
    public int FocusX = 440;                                    // +0x40f0  sharp spot of the "depth of field" blur
    public int FocusY = 40;                                     // +0x40f4
    public int GlowX = 160;                                     // +0x40f8  centre of the additive light glow
    public int GlowY = 250;                                     // +0x40fc
    public readonly float[] Light = [9.0f, 7.5f, 5.0f];         // +0x4100  glow colour added to channel 0 (R), 1 (G), 2 (B)
    public float BlurScale = 0.008f;                            // +0x410c  blur sigma grows by BlurScale * dist^2
    public float LightLevel;                                    // +0x4110  written by the thread, never read

    /// <summary>Port addition: paf's rand() lives in the runtime.</summary>
    private readonly PspRuntime _runtime;

    /// <summary>0x113f4. The field stores (focus, glow, light, blurScale...) are the member initialisers.</summary>
    public Canvas(in CanvasDesc desc, PspRuntime runtime)
    {
        Desc = desc;
        _runtime = runtime;
        SandMath.BuildGaussianBytes(BitsFloat(0x3e428f5c) /*0.19*/, GaussBytes, 11, 11);
        for (var i = 0; i < 34; i++)
        {
            var s = (float)i;
            s = s * BitsFloat(0x3f4ccccd) /*0.8*/ * s + BitsFloat(0x38d1b717) /*0.0001*/;
            SandMath.BuildGaussianKernel(BitsFloat(0x3fe38e39) /*1.7777778*/ / ((s + s) * s),
                Kernels.AsSpan(i * 121, 121), 11, 11);
        }
    }

    // clamp an int to 0..255 the way the original does (two independent movz/movn)
    private static uint Clamp255(int v)
    {
        uint r = 0;
        if (v >= 0) r = (uint)v;
        if (v > 255) r = 255;
        return r;
    }

    /// <summary>
    /// 0x115b8: repaints every pixel: rgba = palette[height] + glow * light (clamped per channel).
    /// <paramref name="unusedImage"/> (the decoded embedded JPEG) is passed by the caller but never read.
    /// </summary>
    public void RenderAll(uint[] palette, uint[] unusedImage)
    {
        _ = unusedImage;
        var w = Desc.Width;
        var h = Desc.Height;
        var kx = 2.0f / (float)w;
        var ky = 2.0f / (float)h;
        var heightMap = Desc.HeightMap;
        var rgba = Desc.Rgba;
        float l0 = Light[0], l1 = Light[1], l2 = Light[2];
        for (var y = 0; y < Desc.Height; y++)
        {
            var fy = (float)(y - GlowY);
            fy = (fy + fy) * ky;
            var dy2 = fy * fy;
            for (var x = 0; x < Desc.Width; x++)
            {
                var fx = (float)(x - GlowX);
                fx = (fx + fx) * kx;
                var glow = 15.0f - (fx * fx + dy2);
                if (glow < 0.0f) glow = 0.0f;
                var idx = x + y * Desc.Width;
                var c = palette[heightMap[idx]];
                var c0 = (int)((float)(int)(c & 0xff) + glow * l0);
                var c1 = (int)((float)(int)((c >> 8) & 0xff) + glow * l1);
                var c2 = (int)((float)(int)((c >> 16) & 0xff) + glow * l2);
                rgba[idx] = Clamp255(c2) << 16 | Clamp255(c1) << 8 | Clamp255(c0) | 0xff000000u;
            }
        }
    }

    /// <summary>
    /// 0x117e4: repaints 512 random pixels with a 7x7 separable Gaussian blur of the palette colours; the
    /// blur radius grows with the distance from (FocusX, FocusY). Plus the additive glow around
    /// (GlowX, GlowY). <paramref name="unusedImage"/> is never read.
    /// </summary>
    public void RenderRandomPixels(uint[] palette, uint[] unusedImage)
    {
        _ = unusedImage;
        var kx = 2.0f / (float)Desc.Width;
        var ky = 2.0f / (float)Desc.Height;
        var fxPos = FocusX; // read once before the loop
        var fyPos = FocusY;
        var width = Desc.Width;
        var height = Desc.Height;
        var heightMap = Desc.HeightMap;
        var rgba = Desc.Rgba;
        float l0 = Light[0], l1 = Light[1], l2 = Light[2];
        Span<float> rows = stackalloc float[7 * 3];
        Span<float> gauss = stackalloc float[4];

        for (var n = 0x1ff; n >= 0; n--) // 512 pixels
        {
            var x = _runtime.Rand() % 480;
            var y = _runtime.Rand() % 272;

            // additive glow around (GlowX, GlowY)
            var gx = (float)(x - GlowX);
            var gy = (float)(y - GlowY);
            gx = (gx + gx) * kx;
            gy = (gy + gy) * ky;
            var glow = 15.0f - (gx * gx + gy * gy);
            if (glow < 0.0f) glow = 0.0f;

            // blur strength: sigma = BlurScale * dist^2(focus) + 0.0001, coef = 1 / (2 sigma^2)
            var dx = (float)(x - fxPos);
            var dy = (float)(y - fyPos);
            dx = (dx + dx) * kx;
            dy = (dy + dy) * ky;
            var sigma = BlurScale * (dx * dx + dy * dy) + BitsFloat(0x38d1b717);
            var coef = 1.0f / ((sigma + sigma) * sigma);

            // Port: the original evaluates Exp(-(i*i) * coef) inside each of the 8 inner loops (7 rows +
            // the vertical pass); it is a pure function of (i, coef), so the 3 values are computed once.
            for (var i = 1; i < 4; i++) gauss[i] = Exp(-(float)(i * i) * coef);

            // horizontal pass on rows y-3..y+3 (rows clamped to the image)
            for (var r = -3; r < 4; r++)
            {
                var yy = y + r;
                if (yy < 0)
                    yy = 0;
                else if (yy >= height)
                    yy = height - 1;
                var rowBase = yy * width;
                var c = palette[heightMap[x + rowBase]];
                var s0 = (float)(int)(c & 0xff);
                var s1 = (float)(int)((c >> 8) & 0xff);
                var s2 = (float)(int)((c >> 16) & 0xff);
                var wsum = 1.0f;
                for (var i = 1; i < 4; i++)
                {
                    var g = gauss[i];
                    var left = x - i;
                    var right = x + i;
                    wsum = wsum + (g + g);
                    // NOTE: the original tests (x - i < i), not (x - i < 0); and only clamps the right
                    // neighbour when the left one was not clamped.
                    if (left < i)
                        left = 0;
                    else if (right >= width)
                        right = width - 1;
                    var a = palette[heightMap[left + rowBase]];
                    var b = palette[heightMap[right + rowBase]];
                    s0 = s0 + g * (float)(int)((a & 0xff) + (b & 0xff));
                    s1 = s1 + g * (float)(int)(((a >> 8) & 0xff) + ((b >> 8) & 0xff));
                    s2 = s2 + g * (float)(int)(((a >> 16) & 0xff) + ((b >> 16) & 0xff));
                }
                var inv = 1.0f / wsum;
                var o = (r + 3) * 3;
                rows[o + 2] = s2 * inv;
                rows[o + 0] = s0 * inv;
                rows[o + 1] = s1 * inv;
            }

            // vertical pass
            {
                var s0 = rows[9 + 0];
                var s1 = rows[9 + 1];
                var s2 = rows[9 + 2];
                var wsum = 1.0f;
                for (var i = 1; i < 4; i++)
                {
                    var g = gauss[i];
                    var up = (3 - i) * 3;
                    var dn = (3 + i) * 3;
                    var t1 = rows[up + 1] + rows[dn + 1];
                    var t0 = rows[up + 0] + rows[dn + 0];
                    var t2 = rows[up + 2] + rows[dn + 2];
                    wsum = wsum + (g + g);
                    s0 = s0 + g * t0;
                    s1 = s1 + g * t1;
                    s2 = s2 + g * t2;
                }
                var inv = 1.0f / wsum;
                var c1 = (int)(s1 * inv + glow * l1);
                var c2 = (int)(s2 * inv + glow * l2);
                var c0 = (int)(s0 * inv + glow * l0);
                rgba[x + y * width] = Clamp255(c2) << 16 | Clamp255(c1) << 8 | Clamp255(c0) | 0xff000000u;
            }
        }
    }
}

/// <summary>One oscillator of a walker (vtable 0x14928, dtor 0x11d74 / 0x11d84; 0x20 bytes). The render
/// thread keeps 45 of them; each walker owns 15.</summary>
internal struct Wave
{
    // +0x00 vtable 0x14928
    public int T;          // +0x04  copy of the walker's step counter
    public float Amp;      // +0x08  45 .. 75 (radius in pixels)
    public float AmpFreq;  // +0x0c  0.001 .. 0.011 (radius modulation speed)
    public float FreqX;    // +0x10  0.001 .. 0.011
    public float FreqY;    // +0x14  = FreqX
    public float AmpScale; // +0x18  0.001 .. 2.001
    public float Scale;    // +0x1c  set to 0.5 on every use (uninitialised before)
}

/// <summary>Built on the thread's stack (sp+0x4890) and passed to <see cref="Walker.Update"/>.</summary>
internal struct WalkInfo
{
    public float StereoDiff; // +0x00  message +0x14 (unused by the walker)
    public float LevelDb;    // +0x04  message +0x0c (unused by the walker)
    public int Steps;        // +0x08  (int)((levelDb + 40) * 0.035) + 1
}

/// <summary>
/// A wandering pen that bumps the height map along 15 rotating offsets around its position (vtable
/// 0x14910: Update 0x11de4, dtor 0x11dac / 0x11dbc; 0x40 bytes).
/// </summary>
internal sealed class Walker
{
    // +0x00 vtable 0x14910
    public Wave[] Waves = [];                      // +0x04  15 consecutive Wave objects (Waves[WaveBase..])
    public int WaveBase;                           //        port: index of the first one in the shared array
    public int WaveCount;                          // +0x08  15
    public int Phase;                              // +0x0c  rand()
    public int Margin;                             // +0x10  30 + rand() % 80 (only used for the start position)
    public float Vx, Vy;                           // +0x14 / +0x18
    public float X, Y;                             // +0x1c / +0x20
    public readonly float[] Unused24 = new float[3]; // +0x24..+0x2c  0.001 + rand/32767 * 0.01, never read
    public readonly float[] Unused30 = new float[3]; // +0x30..+0x38  rand/32767 * 3 (same value x3), never read
    public int Counter;                            // +0x3c  step counter

    /// <summary>0x11de4. <paramref name="frameCounter"/> is passed but not used.</summary>
    public void Update(Canvas canvas, ref int frameCounter, in WalkInfo info, PspRuntime runtime)
    {
        for (var step = info.Steps; step > 0; step--)
        {
            if (Counter % 60 == 0)
            {
                // every 60 steps: steer (velocity nudged along a slowly rotating direction)
                var c = Cos((float)Phase + (float)Counter * 0.001f);
                Vx = Vx + c * 0.004f;
                var s = Sin((float)Phase + (float)Counter * 0.001f);
                Vy = Vy + s * 0.004f;
                if (0.0f < Vx ? 1.0f < Vx : Vx < -1.0f) Vx = Vx * 0.5f;
                if (0.0f < Vy ? 1.0f < Vy : Vy < -1.0f) Vy = Vy * 0.5f;
            }
            else
            {
                // otherwise: friction and move; jump to a random spot when leaving the screen
                var dvx = Vx * BitsFloat(0xb8d1b717); // -0.0001
                var dvy = Vy * BitsFloat(0xb8d1b717);
                Vx = Vx + dvx;
                Vy = Vy + dvy;
                X = X + Vx;
                Y = Y + Vy;
                if (X < 0.0f || 480.0f < X)
                {
                    X = (float)(runtime.Rand() % 480);
                    var r = runtime.Rand();
                    Vy = 0.0f;
                    Vx = 0.0f;
                    Y = (float)(r % 272);
                }
                if (Y < 0.0f || 272.0f < Y)
                {
                    X = (float)(runtime.Rand() % 480);
                    var r = runtime.Rand();
                    Vy = 0.0f;
                    Vx = 0.0f;
                    Y = (float)(r % 272);
                }
            }

            // every oscillator deposits one unit of height at its current offset from the pen
            var width = canvas.Desc.Width;
            var height = canvas.Desc.Height;
            var map = canvas.Desc.HeightMap;
            for (var i = 0; i < WaveCount;)
            {
                ref var wv = ref Waves[WaveBase + i];
                var t = Counter;
                wv.Scale = 0.5f;
                ++i;
                wv.T = t;
                var px0 = X;
                var py0 = Y;
                var s = Sin((float)t * wv.AmpFreq);
                s = s + 1.5f;
                var amp = wv.Amp * ((wv.AmpScale * s) * wv.Scale);
                var px = (int)(amp * SandMath.PolyCos((float)wv.T * wv.FreqX) + px0);
                var py = (int)(amp * SandMath.PolySin((float)wv.T * wv.FreqY) + py0);
                if (px < 0) continue;
                if (px >= width || py < 0) continue;
                if (py >= height) continue;
                var cell = px + py * width;
                if ((int)map[cell] + 1 < 0x100) map[cell] = (byte)(map[cell] + 1);
            }
            ++Counter;
        }
    }
}

/// <summary>
/// Colour schemes (4-byte objects, vtable = 3 virtual methods, no virtual destructor). Palette entries
/// are 0xAABBGGRR (GU 8888).
/// </summary>
internal abstract class Scheme
{
    /// <summary>slot 0</summary>
    public abstract void BuildPalette(uint[] palette, int count);

    /// <summary>slot 1. (Port: the runtime is passed for paf's rand().)</summary>
    public abstract void FillHeightmap(PspRuntime runtime, byte[] map, int width, int height);

    /// <summary>slot 2</summary>
    public abstract void GetLight(out float r, out float g, out float b);
}

/// <summary>vtable 0x14958: light grey -> dark red -> almost black; no height map; light 8,8,8.</summary>
internal sealed class AshScheme : Scheme
{
    /// <summary>0x111a4: three RGB keys, 0..0.5 key0->key1, 0.5..0.95 key1->key2, above: key2.</summary>
    public override void BuildPalette(uint[] palette, int count)
    {
        var k0 = BitsFloat(0x3f666666); // 0.9 (r = g = b)
        float k1r = BitsFloat(0x3f0d8d8e), k1g = BitsFloat(0x3e088889), k1b = BitsFloat(0x3dd0d0d1);
        float k2r = BitsFloat(0x3d40c0c1), k2g = BitsFloat(0x3ce0e0e1), k2b = BitsFloat(0x3c008081);
        var o = 0;
        for (var i = 0; i < count;)
        {
            var t = (float)i / (float)(count - 1);
            float r, g, b;
            if (t < 0.5f)
            {
                var u = t + t;
                var v = 1.0f - u;
                b = v * k0 + u * k1b;
                r = v * k0 + u * k1r;
                g = v * k0 + u * k1g;
            }
            else
            {
                g = k2g;
                b = k2b;
                r = k2r;
                if (t <= BitsFloat(0x3f733333) /*0.95*/)
                {
                    var u = (t - 0.5f) / BitsFloat(0x3ee66666) /*0.45*/;
                    var v = 1.0f - u;
                    b = v * k1b + u * k2b;
                    r = v * k1r + u * k2r;
                    g = v * k1g + u * k2g;
                }
            }
            ++i;
            palette[o++] = (uint)(int)(b * 255.0f) << 16 | (uint)(int)(g * 255.0f) << 8 |
                           (uint)(int)(r * 255.0f) | 0xff000000u;
        }
    }

    /// <summary>0x113b4 (empty)</summary>
    public override void FillHeightmap(PspRuntime runtime, byte[] map, int width, int height) { }

    /// <summary>0x10a4c</summary>
    public override void GetLight(out float r, out float g, out float b)
    {
        r = 8.0f;
        g = 8.0f;
        b = 8.0f;
    }
}

/// <summary>vtable 0x14970: sand gradient (230,210,132) -> (168,132,50); zero height map; light 0.2.</summary>
internal sealed class SandScheme : Scheme
{
    /// <summary>0x11080</summary>
    public override void BuildPalette(uint[] palette, int count)
    {
        var o = 0;
        for (var i = 0; i < count;)
        {
            var t = (float)i / (float)(count - 1);
            ++i;
            var v = 1.0f - t;
            var b = (int)(v * 230.0f + t * 168.0f);
            var g = (int)(v * 210.0f + t * 132.0f);
            var r = (int)(v * 132.0f + t * 50.0f);
            palette[o++] = (uint)b << 16 | (uint)g << 8 | (uint)r | 0xff000000u;
        }
    }

    /// <summary>0x11144</summary>
    public override void FillHeightmap(PspRuntime runtime, byte[] map, int width, int height)
    {
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) map[y * width + x] = 0;
    }

    /// <summary>0x11188</summary>
    public override void GetLight(out float r, out float g, out float b)
    {
        r = BitsFloat(0x3e4ccccd); // 0.2
        g = BitsFloat(0x3e4ccccd);
        b = BitsFloat(0x3e4ccccd);
    }
}

/// <summary>vtable 0x14988: black -> (100,200,252) sky blue; zero height map; light 4,4,4.</summary>
internal sealed class SkyScheme : Scheme
{
    /// <summary>0x10fac (the ctor 0x8238 contains an inlined copy of this loop for count = 256).</summary>
    public override void BuildPalette(uint[] palette, int count)
    {
        var div = count - 1;
        for (var i = 0; i < count; i++)
        {
            var r = (i * 100) / div;
            var g = (i * 200) / div;
            var b = (i * 252) / div;
            palette[i] = (uint)b << 16 | (uint)g << 8 | (uint)r | 0xff000000u;
        }
    }

    /// <summary>0x11024</summary>
    public override void FillHeightmap(PspRuntime runtime, byte[] map, int width, int height)
    {
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) map[y * width + x] = 0;
    }

    /// <summary>0x11068</summary>
    public override void GetLight(out float r, out float g, out float b)
    {
        r = 4.0f;
        g = 4.0f;
        b = 4.0f;
    }
}

/// <summary>vtable 0x149a0: HSV earth gradient (pale grey -> olive/brown -> dark), noisy vertical ramp
/// height map; light 9, 7.5, 5. The only scheme that is actually displayed.</summary>
internal sealed class EarthScheme : Scheme
{
    // 0x10a64: four HSV keys (0..1 each, objects with vtable 0x149b8 on the stack):
    //   key0 (0, 0, 0.9) - key1 (40/255, 240/255, 120/255) - key2 (30/255, 221/255, 65/255)
    //   - key3 (20/255, 200/255, 25/255); segments 0..0.35, 0.35..0.65, 0.65..0.98, then key3.
    private static readonly float[] KeyH = [0.0f, BitsFloat(0x3e20a0a1), BitsFloat(0x3df0f0f1), BitsFloat(0x3da0a0a1)];
    private static readonly float[] KeyS = [0.0f, BitsFloat(0x3f70f0f1), BitsFloat(0x3f5dddde), BitsFloat(0x3f48c8c9)];
    private static readonly float[] KeyV = [BitsFloat(0x3f666666), BitsFloat(0x3ef0f0f1), BitsFloat(0x3e828283), BitsFloat(0x3dc8c8c9)];

    /// <summary>0x10a64</summary>
    public override void BuildPalette(uint[] palette, int count)
    {
        var k035 = BitsFloat(0x3eb33333);
        var k065 = BitsFloat(0x3f266666);
        var o = 0;
        for (var i = 0; i < count;)
        {
            var t = (float)i / (float)(count - 1);
            int a;
            float u;
            float h, s, v;
            var lerp = true;
            if (t < k035)
            {
                u = t / k035;
                a = 0;
            }
            else if (t <= k065)
            {
                u = (t - k035) / BitsFloat(0x3e999999); // 0.29999998
                a = 1;
            }
            else if (t <= BitsFloat(0x3f7ae148)) // 0.98
            {
                u = (t - k065) / BitsFloat(0x3ea8f5c4); // 0.33
                a = 2;
            }
            else
            {
                a = 3;
                u = 0.0f;
                lerp = false;
            }
            if (lerp)
            {
                var w = 1.0f - u;
                var b = a + 1;
                v = w * KeyV[a] + u * KeyV[b];
                h = w * KeyH[a] + u * KeyH[b];
                s = w * KeyS[a] + u * KeyS[b];
            }
            else
            {
                h = KeyH[3];
                s = KeyS[3];
                v = KeyV[3];
            }
            var sat = FloatToU32(s * 255.0f); // float -> unsigned
            var hue = (int)(h * 359.0f);
            var val = FloatToU32(v * 255.0f);
            ++i;
            palette[o++] = VisColor.HsvToRgb(hue, (byte)(sat & 0xff), (byte)(val & 0xff)) | 0xff000000u;
        }
    }

    /// <summary>
    /// 0x10db8: vertical ramp + noise. Row value ((y - h/2) / 200 + 1.36) / 2.72 * 0.255, plus
    /// rand() % 65536 / 65535 * 0.05 per pixel, times 255. Values land around 16..61.
    /// </summary>
    public override void FillHeightmap(PspRuntime runtime, byte[] map, int width, int height)
    {
        var half = height / 2;
        for (var y = 0; y < height; y++)
        {
            if (width <= 0) continue;
            var p = y * width;
            var baseValue = (((float)(y - half) / 200.0f + BitsFloat(0x3fae147b)) / BitsFloat(0x402e147b)) *
                            BitsFloat(0x3e828f5c);
            for (var n = width; n != 0; n--)
            {
                var r = runtime.Rand() % 0x10000;
                var v = (baseValue + ((float)r / 65535.0f) * BitsFloat(0x3d4ccccd)) * 255.0f;
                map[p++] = (byte)FloatToU32(v);
            }
        }
    }

    /// <summary>0x10f84</summary>
    public override void GetLight(out float r, out float g, out float b)
    {
        r = 9.0f;
        g = 7.5f;
        b = 5.0f;
    }
}

/// <summary>
/// Grid of random floats (vtable 0x14a40; ctor 0xf710, dtor 0xf768 / 0xf790, fill 0xf7cc). Type 7 creates
/// a 240x136 one at +0x88 and never reads it, but the fill consumes 32640 rand() values in the ctor.
/// </summary>
internal sealed class RandomField
{
    // +0x00 vtable 0x14a40
    public readonly int Width;    // +0x04
    public readonly int Height;   // +0x08
    public readonly float[] Data; // +0x0c  memalign(0x40, w*h*4)

    /// <summary>0xf710</summary>
    public RandomField(PspRuntime runtime, int width, int height)
    {
        Width = width;
        Height = height;
        Data = new float[width * height];
        Fill(runtime);
    }

    /// <summary>0xf7cc: data[i] = rand() / 32767.</summary>
    public void Fill(PspRuntime runtime)
    {
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++) Data[x + y * Width] = (float)runtime.Rand() / 32767.0f;
    }
}
