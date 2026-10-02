using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.PSP.Common;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;

namespace PlattaPlayer.Visualizations.PSP.Visualizers;

// Particle emitters of the type 3 visualizer ("clouds"); RingEmitter is also the only emitter of the
// unused variant 0. src/vis/sprite_trails/trail_emitters.*.

/// <summary>One particle of the history buffer (0x10 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Particle
{
    public float X;  // +0x00  screen units (480x272 space, +-margins)
    public float Y;  // +0x04
    public float Vx; // +0x08
    public float Vy; // +0x0c
}

/// <summary>
/// Abstract emitter interface (vtable slots 0..2, dtors at 3..4). The owner writes the public fields
/// before every Emit (0x451c / 0x48b4).
///
/// Each frame the visualizer asks one emitter to fill the newest history row with 2*n particles
/// (<see cref="Emit"/>), then every row of the history is advanced (<see cref="Update"/>) and drawn as
/// textured sprites (<see cref="Draw"/>). <c>age</c> is how many frames ago the row was emitted (0 = this
/// frame, rows-1 = oldest); most effects use the parabola env = 4f(1-f), f = age/rows, which is 0 for a
/// new row, 1 halfway through and 0 again when the row is recycled.
/// </summary>
public abstract class TrailEmitter
{
    protected static readonly float TwoPi = BitsFloat(0x40c90fdb);      // 6.2831855
    protected static readonly float Pi = BitsFloat(0x40490fdb);         // 3.1415927
    protected static readonly float Inv70 = BitsFloat(0x3c6a0ea1);      // 0.014285714 = 1/70
    protected static readonly float Inv32768 = BitsFloat(0x38000000);   // 3.0517578e-05
    protected static readonly float WideWidth = BitsFloat(0x440d2d2d);  // 564.7059

    /// <summary>+0x04: owner's frame counter.</summary>
    public uint Frame;

    /// <summary>+0x08: read for owner->brightness (+0x24).</summary>
    public Visualizer? Owner;

    /// <summary>+0x0c: owner's pulse countdown (70 -> 0 after player event 7).</summary>
    public int Pulse;

    /// <summary>+0x10: history length.</summary>
    public int Rows;

    /// <summary>+0x14: n, particles per half row (a row is 2n particles).</summary>
    public int Count;

    /// <summary>slot 0: fills the 2n particles of the new row.</summary>
    public abstract void Emit(Span<Particle> row, PcmBlock pcm, int rowIndex);

    /// <summary>slot 1</summary>
    public abstract void Update(Span<Particle> row, int age, int rowIndex);

    /// <summary>slot 2</summary>
    public abstract void Draw(SpriteBatch batch, Span<Particle> row, int age, int rowIndex);

    /// <summary>Allegrex max(v, -v).</summary>
    protected static int AbsInt(int v) => v < -v ? -v : v;

    /// <summary>1 - (rows - age) / rows, then 4f(1-f) written as f*-4*f + f*4.</summary>
    protected static float AgeEnvelope(int rows, int age)
    {
        var f = 1.0f - (float)(uint)(rows - age) / (float)rows;
        return f * -4.0f * f + f * 4.0f;
    }
}

/// <summary>
/// "Cloud" emitter, vtable 0x147e8, size 0x402c. Emits 2n particles on a wobbling spiral around a moving
/// centre, shaped by the raw PCM samples; old particles drift and fall, and are drawn as 1.45..0.45 x 32
/// unit sprites whose colour is sampled from a 64x64 picture and whose alpha peaks around a moving spot.
/// <list type="bullet">
/// <item>Emit 0x51d4: 36 points at angles i²·(2π/36)² - t, radius 37 + sample*34/32768 (the samples are
/// read as one interleaved L/R sequence). The centre wobbles on a path driven by loudness (Σ|samples|) and
/// the pulse. A cos(frame*0.001) mix blends the spiral with a flat waveform line across the screen. The
/// velocity points outward, skewed by the centre's motion. While seeking (player state 2/3) the centre
/// shifts by ±30/±68.</item>
/// <item>Update 0x5808: drift, gravity (strong during a pulse) and an age-dependent lift.</item>
/// <item>Draw 0x5954: size (1.45 - env)*32, alpha = Lorentzian around a moving spot * env *
/// (0.65·brightness + 0.35), RGB from the 64x64 picture at the particle position shifted by
/// cos(frame*0.004)*32 texels; off-screen particles are skipped.</item>
/// </list>
/// The picture is the JPEG embedded in the PRX at 0x13850 (0x55f bytes, baseline JFIF 64x64), which the
/// original decodes with paf::Image::Open(data, 0x55f, nullptr, 2) + ToBuffer(true) and copies 0x4000
/// bytes of. Here it comes from the user's firmware through <see cref="PspRuntime.Assets"/>.
/// </summary>
public sealed class CloudEmitter : TrailEmitter
{
    private readonly PspRuntime _runtime;
    private readonly uint[] _palette = new uint[64 * 64]; // +0x18  decoded JPEG, RGB * 1.05 (clamped), alpha 0xff
    private float _phase;                                 // +0x4018
    private int _prevX, _prevY;                           // +0x401c, +0x4020  centre of the previous frame
    private int _curX, _curY;                             // +0x4024, +0x4028  centre of this frame (initially 1, 1)

    /// <summary>
    /// 0x5014. If the JPEG is missing or does not decode, the original would dereference a null image and
    /// crash; here the palette stays zero, which the brightening loop turns into opaque black, so the
    /// (additively blended) clouds are invisible but everything else behaves normally. A picture smaller
    /// than 64x64 is copied as far as it goes (the original copies 0x4000 bytes regardless).
    /// </summary>
    public CloudEmitter(PspRuntime runtime)
    {
        _runtime = runtime;
        _curY = 1;
        Frame = 0;
        Owner = null;
        Pulse = 0;
        Rows = 0;
        _phase = 0.0f;
        _prevX = 0;
        _prevY = 0;
        _curX = 1;
        // Decode the 64x64 JPEG and copy its 32-bit pixels (sceKernelMemcpy 0x4000 bytes).
        var image = runtime.OpenJpeg(runtime.Assets.CloudPaletteJpeg);
        if (image is not null)
        {
            var n = Math.Min(image.Pixels.Length, _palette.Length);
            image.Pixels.AsSpan(0, n).CopyTo(_palette);
        }
        // Brighten by 5 %, clamp to 255, force alpha opaque.
        for (var i = 0; i < 64 * 64; i++)
        {
            var c = _palette[i];
            var r = (float)(int)(c & 0xff) * 1.05f + 0.0f;
            var ri = 0xffu;
            if (!(255.0f < r)) ri = (uint)(int)r;
            var g = (float)(int)(c >> 8 & 0xff) * 1.05f + 0.0f;
            var gi = 0xffu;
            if (!(255.0f < g)) gi = (uint)(int)g;
            var b = (float)(int)(c >> 16 & 0xff) * 1.05f + 0.0f;
            var bi = 0xffu;
            if (!(255.0f < b)) bi = (uint)(int)b;
            _palette[i] = bi << 16 | gi << 8 | ri | 0xff000000u;
        }
    }

    /// <summary>0x51d4: fills the 2n particles of the new row.</summary>
    public override void Emit(Span<Particle> row, PcmBlock pcm, int rowIndex)
    {
        var samples = pcm.Samples;
        var n = Count;
        var frameNow = Frame;
        var p = (float)(uint)Pulse * Inv70; // 1 -> 0 after player event 7

        // Sum of |L| and |R| over the first n stereo frames.
        int sumL = 0, sumR = 0;
        for (var i = 0; i < n; i++)
        {
            sumL += AbsInt(samples[i * 2]);
            sumR += AbsInt(samples[i * 2 + 1]);
        }

        var t = (float)frameNow * 0.05f + p * 4.0f * p;      // f30
        var loud = (float)(sumL + sumR) * 1e-05f + 30.0f;    // f1
        var env = p * -4.0f * p + p * 4.0f;                  // f21 (0 when p is 0 or 1)
        var phase = _phase + loud * 0.002f;
        var spread = (loud + 5.0f) * 0.004f;                 // f29
        _prevX = _curX;
        _prevY = _curY;
        _phase = phase;

        // Radius of the centre's path: wobble, plus 90 while a pulse is active.
        var radius = (Sin(t * 0.012f) + 1.2f) * 40.0f * (Cos(t * 0.43f) * 0.5f + 0.8f);
        radius = radius + Sin(p) * 10.1f;
        var angle = phase + p * 24.0f * p;
        radius = (1.0f - env) * radius + env * 90.0f;

        _curX = (int)(radius * Cos(angle) + 240.0f + Sin(angle * 0.04f) * 80.0f);
        _curY = (int)(radius * Sin(angle * 0.8f + t * 0.03f) + 136.0f + Cos(angle * 0.031f + t * 0.01f) * 40.0f);

        // Direction of the centre's movement.
        var dx = (float)(_curX - _prevX);
        var dy = (float)(_curY - _prevY);
        var invLen = 1.0f;
        var len = Sqrt(dx * dx + dy * dy);
        if (1e-05f < len)
        {
            invLen = 1.0f / len;
        }
        else
        {
            dy = 0.7f;
            dx = 0.7f;
        }
        dy = dy * invLen;
        dx = dx * invLen;

        var xOffset = 0; // shifts the cloud while the player seeks
        if (_runtime.PlayerState == 2)
        {
            xOffset = -30;
        }
        else if (_runtime.PlayerState == 3)
        {
            xOffset = 30;
        }

        var total = n * 2;
        var mix = Cos((float)frameNow * 0.001f) * 0.5f + 0.5f; // spiral (1) <-> waveform line (0)
        var invMix = 1.0f - mix;
        var step = TwoPi / (float)total;
        var xOffsetF = (float)xOffset;
        for (var i = 0; i < total; i++)
        {
            var s = (float)samples[i]; // interleaved L/R treated as one sequence
            var a = (float)(i * i) * step * step - t;
            var r = s * Inv32768 * 34.0f + 37.0f;
            var cx = (float)_curX;
            var cy = (float)_curY;
            // spiral point (y snapped to an integer) mixed with a flat waveform across the screen
            var y = mix * (float)(int)(r * Sin(a) + cy) + invMix * (s * Inv32768 * 68.0f + 136.0f);
            var x = mix * (r * Cos(a) + cx + xOffsetF) + (invMix * 480.0f * (float)i) / (float)total;
            row[i].Y = y;
            row[i].X = x;
            var ox = x - cx;
            var oy = mix * (y - cy) + invMix * (y - 136.0f);
            if (_runtime.PlayerState == 2)
            {
                ox = ox + 68.0f;
            }
            else if (_runtime.PlayerState == 3)
            {
                ox = ox - 68.0f;
            }
            // velocity: outward from the centre, minus 20 % of the movement direction
            row[i].Vy = spread * oy - spread * 0.2f * oy * dy;
            row[i].Vx = mix * (spread * ox - spread * 0.2f * ox * dx) + invMix * (ox * 0.04f);
        }
    }

    /// <summary>0x5808: move, with gravity that is strong during a pulse and an age-dependent lift.</summary>
    public override void Update(Span<Particle> row, int age, int rowIndex)
    {
        var p = (float)(uint)Pulse * Inv70;
        p = p * p;
        var total = Count * 2;
        var env = AgeEnvelope(Rows, age);
        var speed = p * 7.0f + 0.9f;
        var gravity = (p + 0.001f) * -49.0f;
        var lift = -(0.5f - env) * 4.5f;
        for (var i = 0; i < total; i++)
        {
            row[i].X = row[i].X + speed * row[i].Vx;
            row[i].Y = row[i].Y + (speed * row[i].Vy + gravity + lift);
        }
    }

    /// <summary>0x5954</summary>
    public override void Draw(SpriteBatch batch, Span<Particle> row, int age, int rowIndex)
    {
        var total = Count * 2;
        var frameNow = Frame;
        var fade = Owner!.Brightness * 0.65f + 0.35f;
        var env = AgeEnvelope(Rows, age);
        var paletteShift = (int)(Cos((float)frameNow * 0.05f * 0.08f) * 32.0f);
        var spotX = (Sin((float)frameNow * 0.01f) + 1.0f) * 240.0f;
        var spotY = (Cos((float)frameNow * 0.02f) + 1.0f) * 50.0f + 180.0f;
        var size = (1.45f - env) * 32.0f; // sprites shrink while the row is mid-life
        var alphaScale = env * fade;
        var minPos = -(size + 10.0f);
        for (var i = 0; i < total; i++)
        {
            var x = row[i].X;
            if (x < minPos || WideWidth < x) continue;
            var y = row[i].Y;
            if (y < minPos || 320.0f < y) continue;
            // alpha: Lorentzian falloff around the moving spot
            var ddx = (x - spotX) * BitsFloat(0x3b088889); // 1/480
            var ddy = (y - spotY) * BitsFloat(0x3b70f0f1); // 1/272
            var alpha = FloatToU32(0.22f / (ddx * ddx + ddy * ddy + 0.22f) * alphaScale * 255.0f);
            // colour: palette pixel under the particle (64x64 over 500x292), shifted over time
            var py = (int)((y + 10.0f) * 64.0f * BitsFloat(0x3b607038)); // 1/292
            var px = (int)((x + 10.0f) * 64.0f * 0.002f);                // 1/500
            var texel = _palette[(py * 64 + px + paletteShift) & 0xfff];
            batch.Add(x, y, x + size, y + size, (texel & 0xffffff) | alpha << 24);
        }
    }
}

/// <summary>
/// "Ring" emitter, vtable 0x14818, size 0x260. Emits two rings of n particles (radius 40 + sample*45/32768,
/// L for ring A, R for ring B) around two centres orbiting the screen centre (radius ≤ 118); particles fly
/// outwards with a speed set by the loudness ((dB+40)*0.018) and bounce (x -0.7) off x∈[-42, 564.7] and
/// y∈[-42, 320]. Each history row has its own pair of colours: Σ|L| and 0xffffff-Σ|R| over 30 frames,
/// &amp; 0xefefef | 0x010101. Drawn as 32-unit sprites, alpha = (env*128)*brightness.
/// </summary>
public sealed class RingEmitter : TrailEmitter
{
    private readonly int[] _unused18 = new int[3]; // +0x18..+0x20  zeroed by the ctor, never read
    private int _centerAX = 1;                     // +0x24  ring A centre
    private int _centerAY = 1;                     // +0x28
    private int _centerBX;                         // +0x2c  ring B centre (not initialised by the ctor)
    private int _centerBY;                         // +0x30
    private readonly uint[] _rowColorA = new uint[64]; // +0x34 + row*8  (sum |L| of 30 frames) & 0xefefef | 0x010101
    private readonly uint[] _rowColorB = new uint[64]; // +0x38 + row*8  (0xffffff - sum |R|) & 0xefefef | 0x010101
    private readonly LevelMeter _meter;            // +0x234  (-40 dB floor, 0.3 s, mono)
    private float _level;                          // +0x25c  meter dB + 40

    /// <summary>0x60c0</summary>
    public RingEmitter()
    {
        _meter = new LevelMeter(-40.0f, 0.3f, true);
        Frame = 0;
        Owner = null;
        Pulse = 0;
        Rows = 0;
        _ = _unused18;
    }

    /// <summary>0x6188</summary>
    public override void Emit(Span<Particle> row, PcmBlock pcm, int rowIndex)
    {
        var n = Count;
        var samples = pcm.Samples;
        var p = (float)(uint)Pulse * Inv70;
        var t = (float)Frame * 0.05f;

        // Colour seeds: sum of |L| and |R| over the first 30 stereo frames.
        var sumL = 0u;
        var sumR = 0;
        for (var i = 0; i < 30; i++)
        {
            sumL += (uint)AbsInt(samples[i * 2]);
            sumR += AbsInt(samples[i * 2 + 1]);
        }

        _meter.Update(pcm);
        _level = _meter.Db() + 40.0f;
        var speed = _level * 0.018f;

        var radius = (Sin(t * 0.12f) + 1.2f) * 60.0f * (Cos(t * 0.71f) * 0.5f + 0.8f) + Sin(p) * 10.0f;
        if (radius < 0.0f)
        {
            radius = 0.0f;
        }
        else if (118.0f < radius)
        {
            radius = 118.0f;
        }

        var step = TwoPi / (float)n;
        var radiusX = radius * BitsFloat(0x3fe1e1e2); // 1.7647059 = 30/17 (480/272)
        var tPi = t + Pi;
        for (var i = 0; i < n; i++)
        {
            // (recomputed identically for every particle in the original)
            _centerAX = (int)(radiusX * Cos(tPi) * Sin(t + t * 0.01f) + 240.0f);
            _centerAY = (int)(radius * Sin(tPi + t * 0.2f) + 136.0f);
            _centerBX = (int)(radiusX * Cos(t + t * 0.11f) + 240.0f);
            _centerBY = (int)(radius * Sin(t) + 136.0f);

            var a = (float)i * step + t;
            var r = (float)samples[i * 2] * Inv32768 * 45.0f + 40.0f;
            var cx = (float)_centerAX;
            var cy = (float)_centerAY;
            var xa = (float)(int)(r * Cos(a) + cx);
            var ya = (float)(int)(r * Sin(a) + cy);
            row[i].X = xa;
            row[i].Y = ya;
            row[i].Vx = speed * (xa - cx);
            row[i].Vy = speed * (ya - cy);

            a = a + 0.08f;
            r = (float)samples[i * 2 + 1] * Inv32768 * 45.0f + 40.0f;
            cx = (float)_centerBX;
            cy = (float)_centerBY;
            var xb = r * Cos(a) + cx;
            var yb = r * Sin(a) + cy;
            row[n + i].X = xb;
            row[n + i].Y = yb;
            row[n + i].Vy = speed * (yb - cy);
            row[n + i].Vx = speed * (xb - cx);
        }

        _rowColorB[rowIndex] = ((0xffffffu - (uint)sumR) & 0xefefefu) | 0x010101u;
        _rowColorA[rowIndex] = (sumL & 0xefefefu) | 0x010101u;
    }

    /// <summary>0x6674: fly outwards; bounce (x0.7) off the screen edges.</summary>
    public override void Update(Span<Particle> row, int age, int rowIndex)
    {
        // _level already includes +40; the original adds 40 again.
        var k = (Sin((float)Frame * 0.05f * 0.13f) * 0.3f + 0.5f) * 0.085f * 0.06f * (_level + 40.0f);
        var total = Count * 2;
        for (var i = 0; i < total; i++)
        {
            var vx = row[i].Vx;
            var vy = row[i].Vy;
            var x = row[i].X + k * vx;
            row[i].X = x;
            row[i].Y = row[i].Y + k * vy;
            if (x < -42.0f || WideWidth < x)
            {
                row[i].Vx = vx * -0.7f;
            }
            var y = row[i].Y;
            if (y < -42.0f || 320.0f < y)
            {
                row[i].Vy = vy * -0.7f;
            }
        }
    }

    /// <summary>0x67f8: 32x32 sprites, alpha 128 * env * brightness, per-row colours.</summary>
    public override void Draw(SpriteBatch batch, Span<Particle> row, int age, int rowIndex)
    {
        var n = Count;
        var env = AgeEnvelope(Rows, age);
        var alpha8 = FloatToU32(env * 128.0f) & 0xff;
        var alpha = FloatToU32((float)(int)alpha8 * Owner!.Brightness);
        var colorB = _rowColorB[rowIndex] | alpha << 24;
        var colorA = _rowColorA[rowIndex] | alpha << 24;
        if (n <= 0) return;
        for (var i = 0; i < n; i++)
        {
            var x = row[i].X;
            var y = row[i].Y;
            batch.Add(x, y, x + 32.0f, y + 32.0f, colorA);
        }
        for (var i = 0; i < n; i++)
        {
            var x = row[n + i].X;
            var y = row[n + i].Y;
            batch.Add(x, y, x + 32.0f, y + 32.0f, colorB);
        }
    }
}
