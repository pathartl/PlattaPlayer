using PlattaPlayer.Visualizations.PSP.Common;
using PlattaPlayer.Visualizations.PSP.Gu;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Visualizers;

/// <summary>
/// Visualizer types 3 and 6, "sprite trails" (src/vis/sprite_trails). One class (ctor 0x3480, object size
/// 0x2210, vtable 0x14788) with looks selected by the ctor argument: Vis_CreateVisualizer (0x17f8) passes
/// <see cref="VariantClouds"/> (2) for type 3 and <see cref="VariantDashedCurves"/> (1) for type 6.
/// <see cref="VariantRings"/> (0) exists in the code but the factory never selects it.
///
/// All looks keep a ring of particle rows (history), draw every particle as an additive, textured GU
/// sprite (u8 uv (0,0)-(0x80,0x80), colour on the 2nd vertex) over a two-colour gradient background, and
/// use one 16x16 soft texture (Gaussian blob / ring blend, PSP-swizzled RGBA8888, RGB white) generated on
/// the CPU.
///
/// <para><b>Common frame</b> (Render 0x4c4c): pulse counts down (set to 70 by player event 7), frame++,
/// brightness copied, LevelMeter (-40 dB floor, 0.3 s, mono) updated; the background colours and texture
/// are refreshed (0x4aa4); the variant fills the sprite batch; then the background quad is drawn with
/// texture and blend off, scaled by s = (cos(frame*0.0007)+1)/2 (clamped to ≤ 0.25 in ring mode), and the
/// batch is drawn translated by (-240, -136) with additive blending
/// (GU_ADD, GU_SRC_ALPHA, GU_FIX 0xffffffff).</para>
///
/// <para><b>Variant 1</b> (type 6, thumbnail music_tex_vis_thum_5): yellow-green gradient, rgb(200,230,0)
/// over rgb(165,185,0). Two opposite points orbit a wobbling 3D torus (Orbit 0x5d9c) whose spin follows
/// the loudness ((dB+40)*0.01 + pulse*1.5; the meter is updated a second time here); they are rotated about
/// Z by cos(t*0.81) and about Y by t (t = frame*0.05) and projected. Each frame the segment each point
/// travelled becomes a row of 8 sprites; 64 rows are kept, alpha = (64-age)/64*0.3*brightness*224, curve A
/// sprites 28.8 units, curve B 25.6. The newest row uses a 40-frame cross-fade between random entries of a
/// 6-colour palette. Older rows take the RGB the batch memory holds at sprite 2(r·n+i) (A) / 2(r·n+i)+1 (B):
/// those indices do not match where rows are written (r·2n+i), so colours get shuffled between rows and
/// curves; this is reproduced exactly (the batch memory is persistent state). Odd rows of curve A are colour
/// 0, which makes the dashes. Texture (0x3bf0): a Gaussian on frame 0 and on frames with bit 3 set.
/// The background "fade" countdown (800, reset by player events 0/1) only changes the alpha byte, which the
/// quad ignores.</para>
///
/// <para><b>Variant 2</b> (type 3, thumbnail music_tex_vis_thum_2): dark green gradient (rgb(117,147,88) /
/// rgb(29,132,88) on top, rgb(6,27,6) / rgb(0,10,1) at the bottom). n = 18, 50 rows, row stride 36
/// particles. Each row is emitted by <see cref="CloudEmitter"/> or (ring mode, n forced to 14)
/// <see cref="RingEmitter"/>; the emitter that made each row updates and draws it every frame. Mode switch:
/// while the background factor is below 0.25 and the frame count exceeds a threshold (starts at 3000),
/// rand()&amp;1 = 1 toggles the mode and resets the threshold, 0 lowers the threshold by 700; either way
/// the count resets. Texture (0x4690): ring/Gaussian alpha blend regenerated 7 of every 8 frames.</para>
///
/// <para><b>Variant 0</b>: ring particles only, black background (colours never set), texture regenerated
/// every frame.</para>
///
/// <para><b>Player events</b> (0x4f74): LoadResources() first; 0/1 (variant 1 only) reset the background
/// countdown; 7 sets pulse = 70; others do nothing.</para>
///
/// The original's globals (the Orbit angles 0x16330..0x16340 and the BackgroundQuad vertex array 0x16400)
/// are per-instance state here.
/// </summary>
public sealed class SpriteTrails : Visualizer
{
    public const int VariantRings = 0;
    public const int VariantDashedCurves = 1;
    public const int VariantClouds = 2;

    public const int MaxSprites = 0xf00; // sprite batch / particle buffer capacity
    public const int TexSize = 16;       // 16x16 RGBA8888, swizzled
    public const int MaxRows = 64;

    private static readonly float TwoPi = BitsFloat(0x40c90fdb); // 6.2831855
    private static readonly float Inv70 = BitsFloat(0x3c6a0ea1); // 0.014285714

    // base Visualizer fields: +0x04 alpha, +0x10 color[4], +0x20 owner, +0x24 brightness
    private readonly int _variant;                       // +0x28
    private int _pointsPerRow;                           // +0x2c  n (a row holds 2n particles)
    private int _rows;                                   // +0x30  history length
    private readonly SpriteBatch _batch;                 // +0x34
    private readonly SpriteVertex[] _batchVertices;      // +0x38  memalign(0x40, 0xf00 * 0x28)
    private readonly Particle[] _particles;              // +0x3c  memalign(0x40, 0xf000): 0xf00 particles
    private uint[]? _texPixels;                          // +0x40  locked pixels of _texSurface (non-null = loaded)
    private readonly uint[] _ringTex = new uint[0x400];  // +0x44   16x16 used (room for 32x32)
    private readonly uint[] _blobTex = new uint[0x400];  // +0x1044 16x16 used
    private PafSurface? _texSurface;                     // +0x2044 SurfaceRCPtr
    private int _head;                                   // +0x2048 newest history row
    private uint _frame;                                 // +0x204c frame counter
    private int _pulse;                                  // +0x2050 set to 70 by player event 7, -1 per frame
    private readonly float[] _segStartA = [0, 0];        // +0x2054 previous position of orbit point A (x, y)
    private readonly float[] _segStartB = [0, 0];        // +0x205c previous position of orbit point B
    private readonly int[] _posA = [1, 1];               // +0x2064 current position of A (screen, int)
    private readonly int[] _posB = [1, 1];               // +0x206c current position of B
    // +0x2074 = 0x78 and +0x2078 = 0 are set by the ctor and never read (omitted).
    private readonly LevelMeter _meter;                  // +0x207c (-40 dB floor, 0.3 s, mono)
    private readonly Orbit _orbit = new();               // +0x20a4 (+0x20a8/+0x20ac unused); state = globals 0x16330..
    private float _brightnessCopy;                       // +0x20b0 brightness at the start of Render
    private readonly uint[] _palette = new uint[6];      // +0x20b4 trail colours (0x00BBGGRR)
    private uint _colorFrom;                             // +0x20cc
    private uint _colorTo;                               // +0x20d0
    private int _colorChangeFrame;                       // +0x20d4
    private int _bgFadeCountdown = 800;                  // +0x20d8 variant 1, reset by player events 0/1
    private readonly BackgroundVertex[] _backgroundVertices = new BackgroundVertex[4]; // global 0x16400
    private readonly BackgroundQuad _background;         // +0x20dc
    private readonly CloudEmitter _cloud;                // +0x20f0
    private readonly RingEmitter _ring;                  // +0x20f4
    private bool _ringMode;                              // +0x20f8 variant 2: emit rings instead of clouds
    private int _modeTimer;                              // +0x20fc
    private int _modeThreshold = 3000;                   // +0x2100
    private readonly TrailEmitter?[] _rowEmitter = new TrailEmitter?[MaxRows]; // +0x2104 variant 2: emitter that filled each row

    // ------------------------------------------------------------------------------------------------
    // construction / resources

    /// <summary>0x3480</summary>
    public SpriteTrails(PspRuntime runtime, int variant) : base(runtime)
    {
        _variant = variant;
        _meter = new LevelMeter(-40.0f, 0.3f, true);
        _background = new BackgroundQuad(_backgroundVertices, 0, 0, 0, 0);
        _batchVertices = new SpriteVertex[SpriteBatch.BufferBytes(MaxSprites) / 0x14];
        _batch = new SpriteBatch(MaxSprites, _batchVertices); // + ignored args (16, 16)
        _cloud = new CloudEmitter(runtime);
        _ring = new RingEmitter();
        _palette[0] = 0x3900e1; // red
        _palette[1] = 0x387768; // olive grey
        _palette[2] = 0x4b0090; // purple
        _palette[3] = 0x2d00a9; // crimson
        _palette[4] = 0xc10011; // blue
        _palette[5] = 0x3c883b; // green
        for (var i = 0; i < MaxRows; i++) _rowEmitter[i] = null;

        _particles = new Particle[0xf000 / 0x10];
        var p = _particles.AsSpan(_head * _pointsPerRow * 2); // = the whole buffer (both still 0)
        for (var i = 0; i < MaxSprites; i++)
        {
            p[i].Y = -30.0f;
            p[i].X = -30.0f;
            p[i].Vy = 0.0f;
            p[i].Vx = 0.0f;
        }
        GenerateRing(5.0f, _ringTex, TexSize, TexSize);
        GenerateGaussian(8.0f, _blobTex, TexSize, TexSize);
        Runtime.Srand(Runtime.SystemTimeLow());
    }

    /// <summary>The ctor argument: <see cref="VariantRings"/>, <see cref="VariantDashedCurves"/> or <see cref="VariantClouds"/>.</summary>
    public int Variant => _variant;

    /// <summary>Variant 2: true while rows are emitted as rings instead of clouds.</summary>
    public bool RingMode => _ringMode;

    /// <summary>0x36e8 (deleting: 0x37bc)</summary>
    public override void Dispose()
    {
        // The original frees the particle and vertex memory and the batch/emitters here.
        UnloadResourcesCore(); // direct (non-virtual) call
        base.Dispose();
    }

    /// <summary>0x3898</summary>
    public override void UnloadResources() => UnloadResourcesCore();

    private void UnloadResourcesCore()
    {
        if (_texPixels is not null)
        {
            _texPixels = null;
            _texSurface!.Unlock();
            _texSurface = null; // 0x10910 assign(nullptr) = ReleaseSurface
        }
    }

    /// <summary>
    /// 0x38f4: paf_Surface_ctor(s, 16, 16, mode 3 (8888), 0, 0, 1, 0, 0). The surface stays locked while
    /// loaded; its pixels are used directly as the GU texture.
    /// </summary>
    public override void LoadResources()
    {
        if (IsLoaded()) return;
        _texSurface = new PafSurface(TexSize, TexSize);
        _texPixels = _texSurface.Lock(0);
        Array.Clear(_texPixels, 0, 0x400 / 4); // sceKernelMemset(0x400)
    }

    /// <summary>0x39a4</summary>
    public override bool IsLoaded() => _texPixels is not null;

    /// <summary>0x4f74 (jump table at 0x13800)</summary>
    public override void OnPlayerEvent(PlayerEvent ev)
    {
        var type = ev.Type;
        LoadResources(); // virtual call (slot +0x1c)
        switch (type)
        {
            case 0:
            case 1:
                if (_variant == VariantDashedCurves)
                {
                    _bgFadeCountdown = 800;
                    UpdateTextureAndBackground(VariantDashedCurves, false);
                }
                break;
            case 7:
                _pulse = 70;
                break;
            default: // 2..6, 8..0x12 and anything else: nothing
                break;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // per frame

    /// <summary>0x4c4c</summary>
    public override void Render(PcmBlock pcm)
    {
        if (_pulse > 0) _pulse = _pulse - 1;
        var bright = Brightness;
        _frame = _frame + 1;
        _brightnessCopy = bright;
        _meter.Update(pcm);
        UpdateTextureAndBackground(_variant, true);

        if (_variant == VariantDashedCurves)
        {
            RenderDashedCurves(pcm);
        }
        else if (_variant == VariantRings)
        {
            RenderRings(pcm);
        }
        else if (_variant == VariantClouds)
        {
            RenderClouds(pcm);
        }
        // sceKernelDcacheWritebackRange(batch vertices, count * 0x28): not needed here.

        var alpha = FloatToU32(bright * 255.0f) & 0xff;
        Gu.Disable(GU_TEXTURE_2D);
        Gu.Color(alpha << 24 | 0xffffff);

        // Background brightness: slow cosine, period ~8976 frames.
        var bgScale = (Cos((float)_frame * 0.0007f) + 1.0f) * 0.5f;
        if (_variant == VariantClouds)
        {
            var timer = _modeTimer + 1;
            _modeTimer = timer;
            // While the background is dark, after _modeThreshold frames flip a coin:
            // heads switches clouds <-> rings, tails waits 700 frames less next time.
            if (bgScale < 0.25f && _modeThreshold < timer)
            {
                if ((Runtime.Rand() & 1) == 0)
                {
                    _modeThreshold = _modeThreshold - 700;
                }
                else
                {
                    _modeThreshold = 3000;
                    _ringMode = !_ringMode;
                }
                _modeTimer = 0;
            }
        }
        if (_ringMode && 0.25f < bgScale) bgScale = 0.25f;

        Gu.Disable(GU_BLEND);
        _background.Draw(Gu, bgScale);
        Gu.Enable(GU_BLEND);
        Gu.Disable(GU_LIGHTING);
        Gu.GumPushMatrix();
        Gu.GumTranslate(-240.0f, -136.0f, 0.0f);
        Gu.BlendFunc(GU_ADD, GU_SRC_ALPHA, GU_FIX, 0, 0xffffffff); // additive
        Gu.Enable(GU_BLEND);
        Gu.Enable(GU_TEXTURE_2D);
        Gu.TexFunc(GU_TFX_MODULATE, GU_TCC_RGBA);
        Gu.TexImage(0, TexSize, TexSize, TexSize, _texPixels!);
        Gu.TexMode(GU_PSM_8888, 0, 0, 1); // swizzled
        Gu.TexOffset(0.0f, 0.0f);
        Gu.TexScale(1.0f, 1.0f);
        _batch.Draw(Gu);
        Gu.GumPopMatrix();
    }

    /// <summary>0x4aa4: background colours + texture refresh for the given variant.</summary>
    private void UpdateTextureAndBackground(int variant, bool flush)
    {
        if (variant == VariantDashedCurves)
        {
            var countdown = _bgFadeCountdown;
            if (countdown > 0)
            {
                countdown = countdown - 1;
                _bgFadeCountdown = countdown;
            }
            // Alpha fades out over the last 40 % of the countdown. BackgroundQuad.Draw ignores the alpha
            // byte (and blending is off for it), so this has no visible effect.
            var a = 0xffu;
            var f = (float)countdown / 800.0f;
            if (!(0.4f < f)) a = FloatToU32(f * 2.5f * 255.0f) & 0xff;
            var top = a << 24 | 0xe6c8;    // rgb(200, 230, 0)
            var bottom = a << 24 | 0xb9a5; // rgb(165, 185, 0)
            _background.SetColors(top, top, bottom, bottom);
            if (_variant != variant) return;
            if (_frame != 0 && (_frame & 8) == 0) return; // regenerate 8 frames out of 16
            UpdateTextureDashed();
        }
        else if (variant == VariantClouds)
        {
            // rgb(117,147,88) / rgb(29,132,88) on top, rgb(6,27,6) / rgb(0,10,1) at the bottom
            _background.SetColors(0xff589375, 0xff58841d, 0xff061b06, 0xff010a00);
            if (_variant != variant) return;
            if (_frame != 0 && (_frame & 7) == 0) return; // 7 frames out of 8
            UpdateTextureClouds();
        }
        else
        {
            // variant 0 (and anything else): background colours untouched (stay 0)
            UpdateTextureRings();
            Gu.TexFlush();
            return;
        }
        if (flush) Gu.TexFlush();
    }

    /// <summary>0x3bf0: variant 1 texture = Gaussian blob, sigma ~1.1..2.1 (+ up to 0.75 when dim).</summary>
    private void UpdateTextureDashed()
    {
        var b = _brightnessCopy;
        var s = Sin((float)_frame * 0.05f);
        GenerateGaussian((s * 0.5f + 2.7f + (1.0f - b) * 1.5f) * 0.5f, _texPixels!, TexSize, TexSize);
    }

    /// <summary>0x43dc: variant 0 texture = blend of a ring and a blob of the same pulsing size.</summary>
    private void UpdateTextureRings()
    {
        var b = _brightnessCopy;
        var t = (float)_frame * 0.05f;
        var dim = 1.0f - b;
        dim = dim + dim;
        GenerateGaussian(Sin(t) + 2.7f + dim, _blobTex, TexSize, TexSize);
        GenerateRing(Sin(t) + 2.7f + dim, _ringTex, TexSize, TexSize);
        BlendAlpha(Sin(t * 0.9f) * 0.5f + 0.5f, _texPixels!, _ringTex, _blobTex, TexSize, TexSize);
    }

    /// <summary>
    /// 0x4690: variant 2 texture. Clouds: wide soft blob (sigma ~3.4..3.6) mixed with a large ring (radius
    /// ~4.9..5.1); rings mode: smaller blob/ring (~1.7..3.7).
    /// </summary>
    private void UpdateTextureClouds()
    {
        var t = (float)_frame * 0.03f;
        var b = _brightnessCopy;
        float ringRadius;
        if (!_ringMode)
        {
            _ = _meter.Db(); // called, result unused
            var a = t * 0.7f;
            var dim = (1.0f - b) * 2.2f;
            GenerateGaussian((Sin(a) * 0.2f + 6.9f + dim) * 0.5f, _blobTex, TexSize, TexSize);
            var dim2 = dim + dim;
            ringRadius = (Sin(a) * 0.2f + 9.9f + dim2) * 0.5f;
        }
        else
        {
            var dim = (1.0f - b) * 1.4f;
            GenerateGaussian(Sin(t) + 2.7f + dim, _blobTex, TexSize, TexSize);
            ringRadius = Sin(t) + 2.7f + dim;
        }
        GenerateRing(ringRadius, _ringTex, TexSize, TexSize);
        BlendAlpha(Sin(t * 0.6f) * 0.3f + 0.3f, _texPixels!, _ringTex, _blobTex, TexSize, TexSize);
    }

    // ------------------------------------------------------------------------------------------------
    // variant 1: dashed curves

    /// <summary>0x39b0: rotate (x, z) of v by angle.</summary>
    private static void RotateY(float angle, Span<float> v)
    {
        var z = v[2] * Cos(angle) - v[0] * Sin(angle);
        var x = v[2] * Sin(angle) + v[0] * Cos(angle);
        v[2] = z;
        v[0] = x;
    }

    /// <summary>0x3a38: rotate (x, y) of v by angle.</summary>
    private static void RotateZ(float angle, Span<float> v)
    {
        var x = v[0] * Cos(angle) - v[1] * Sin(angle);
        var y = v[0] * Sin(angle) + v[1] * Cos(angle);
        v[0] = x;
        v[1] = y;
    }

    /// <summary>0x3ac0: per-channel t * to + (1 - t) * from on 0x00BBGGRR colours (result alpha 0).</summary>
    private static uint LerpColor(float t, uint from, uint to)
    {
        var u = 1.0f - t;
        var r = FloatToU32(t * (float)(int)(to & 0xff) + u * (float)(int)(from & 0xff)) & 0xff;
        var g = FloatToU32(t * (float)(int)(to >> 8 & 0xff) + u * (float)(int)(from >> 8 & 0xff)) & 0xff;
        var b = FloatToU32(t * (float)(int)(to >> 16 & 0xff) + u * (float)(int)(from >> 16 & 0xff)) & 0xff;
        return b << 16 | g << 8 | r;
    }

    /// <summary>
    /// Two points orbiting a torus (0x14808 object, vtable only; ctor 0x5d54, dtors 0x5d64 / 0x5d74). Its
    /// state lives in globals 0x16330..0x16340 in the original (zero at load, shared by all instances);
    /// here it is per instance.
    /// </summary>
    private sealed class Orbit
    {
        private float _radiusPhase; // 0x16330
        private float _spin;        // 0x16334  rotation of the ring plane, speed = audio
        private float _around;      // 0x16338  position of the points around the ring
        private float _tubePhase;   // 0x1633c
        private float _tilt;        // 0x16340

        private static void WrapTwoPi(ref float a)
        {
            if (TwoPi < a) a = a - TwoPi;
        }

        /// <summary>
        /// 0x5d9c: two opposite points on a ring of radius 14 + 14 sin(tube) around a circle of radius
        /// R = 80 + 20 sin(...), tilted by 0.7 sin(tilt) and spun by <paramref name="speed"/>. The original
        /// advances three of the angles 16 times per call and calls Sin() on the tilt angle each time
        /// without using the result.
        /// </summary>
        public void Step(float speed, Span<float> p0, Span<float> p1)
        {
            _radiusPhase = _radiusPhase + BitsFloat(0x3be56042); // 0.007
            WrapTwoPi(ref _radiusPhase);
            var bigR = Sin(_radiusPhase) * 20.0f + 80.0f;
            _tubePhase = _tubePhase + BitsFloat(0x3d0b4396); // 0.034
            WrapTwoPi(ref _tubePhase);
            var r0 = Sin(_tubePhase) * 14.0f + 14.0f;
            var r1 = -(Sin(_tubePhase) * 14.0f + 14.0f);
            var tilt = Sin(_tilt) * 0.7f;

            float sa = Sin(_around), ca = Cos(_around);
            float st = Sin(tilt), ct = Cos(tilt);
            float ss = Sin(_spin), cs = Cos(_spin);
            p0[0] = r0 * sa + bigR * st;
            p0[1] = (r0 * ca + bigR * ct) * cs;
            p0[2] = (r0 * ca + bigR * ct) * ss;
            p1[0] = r1 * sa + bigR * st;
            p1[1] = (r1 * ca + bigR * ct) * cs;
            p1[2] = (r1 * ca + bigR * ct) * ss;

            var spinStep = speed * BitsFloat(0x3bc90fdb); // 2*pi/1024
            for (var i = 0; i < 16; i++)
            {
                _around = _around + BitsFloat(0x3bdeb6fc); // 0.006796716
                _spin = _spin + spinStep;
                _tilt = _tilt + BitsFloat(0x3ab8030b);     // 0.0014038993
                WrapTwoPi(ref _around);
                WrapTwoPi(ref _spin);
                WrapTwoPi(ref _tilt);
                _ = Sin(_tilt); // result unused in the original
            }
        }
    }

    /// <summary>0x3cac</summary>
    private void RenderDashedCurves(PcmBlock pcm)
    {
        _pointsPerRow = 8;
        var p = (float)_pulse * Inv70;
        _rows = 64;
        _head = (_head + 1) & 0x3f;
        var t = (float)_frame * 0.05f;
        // The original calls Sin(t) twice here and discards the results.
        _meter.Update(pcm); // second meter update this frame (Render already did one)
        var level = _meter.Db() + 40.0f;
        // The original also Hann-windows the first n stereo frames of pcm into a stack array that is
        // never read (dead code, omitted).

        Span<float> p0 = stackalloc float[4]; // stack 0x230 (x, y, z)
        Span<float> p1 = stackalloc float[4]; // stack 0x240
        _orbit.Step(level * 0.01f + p * 1.5f, p0, p1);
        var tilt = Cos(t * 0.81f);
        RotateZ(tilt, p0);
        RotateZ(tilt, p1);
        RotateY(t, p0);
        RotateY(t, p1);

        // Segment each point travelled since the previous frame (positions snapped to ints).
        var ax = (int)(p0[0] + 240.0f);
        var ay = (int)(p0[1] + 136.0f);
        var dxA = (float)ax - (float)_posA[0];
        var dyA = (float)ay - (float)_posA[1];
        _segStartB[0] = (float)_posB[0];
        _segStartB[1] = (float)_posB[1];
        _posB[0] = (int)(p1[0] + 240.0f);
        _posB[1] = (int)(p1[1] + 136.0f);
        _segStartA[0] = (float)_posA[0];
        _segStartA[1] = (float)_posA[1];
        _posA[0] = ax;
        _posA[1] = ay;
        if (!(1e-05f < Sqrt(dxA * dxA + dyA * dyA)))
        {
            dyA = 0.7f;
            dxA = 0.7f;
        }
        var dxB = (float)_posB[0] - _segStartB[0];
        var dyB = (float)_posB[1] - _segStartB[1];
        if (!(1e-05f < Sqrt(dxB * dxB + dyB * dyB)))
        {
            dyB = 0.7f;
            dxB = 0.7f;
        }

        _batch.Reset(0);

        // Trail colour: cross-fade over 40 frames, then pick the next palette entry.
        var now = _frame;
        var elapsed = (int)(now - (uint)_colorChangeFrame);
        var f = (float)elapsed / 40.0f;
        if (1.0f < f) f = 1.0f;
        var color = LerpColor(f, _colorFrom, _colorTo);
        if (!(elapsed < 41))
        {
            _colorChangeFrame = (int)now;
            _colorFrom = _colorTo;
            _colorTo = _palette[Runtime.Rand() % 6];
        }

        // New row: n points evenly along each segment.
        var n = _pointsPerRow;
        var inv = 1.0f / (float)n;
        var row = Row(_head);
        for (var i = 0; i < n; i++)
        {
            var s = inv * (float)i;
            row[i].X = _segStartA[0] + s * dxA;
            row[i].Vx = 0.0f;
            row[i].Vy = 0.0f;
            row[i].Y = _segStartA[1] + s * dyA;
            row[n + i].X = _segStartB[0] + s * dxB;
            row[n + i].Vx = 0.0f;
            row[n + i].Vy = 0.0f;
            row[n + i].Y = _segStartB[1] + s * dyB;
        }

        // Draw all rows, alpha falling linearly with age.
        // For rows other than the newest, the colour is taken from whatever the sprite batch memory holds
        // at sprite index 2*(r*n+i) (curve A) / 2*(r*n+i)+1 (curve B). Those indices do not match where
        // the row's sprites are written (r*2n+i / r*2n+n+i), so they read a mix of last frame's and this
        // frame's colours; reproduced as is. Odd rows of curve A are made fully transparent -> dashes.
        var rows = _rows;
        var sizeA = BitsFloat(0x41e66666); // 28.8
        var sizeB = BitsFloat(0x41cccccd); // 25.6
        for (var r = 0; r < rows; r++)
        {
            var ageMask = (_head - r) & (rows - 1);
            var alpha = FloatToU32((float)(rows - ageMask) / (float)rows * 0.3f * _brightnessCopy * 224.0f) & 0xff;
            var src = Row(r);
            for (var i = 0; i < n; i++)
            {
                var c = color | alpha << 24;
                if (r != _head)
                {
                    c = (_batch.SpriteColor((r * n + i) * 2) & 0xffffff) | alpha << 24;
                    if ((r & 1) != 0) c = 0;
                }
                var x = src[i].X;
                var y = src[i].Y;
                _batch.Add(x, y, x + sizeA, y + sizeA, c);
            }
            for (var i = 0; i < n; i++)
            {
                var c = color;
                if (r != _head)
                {
                    c = _batch.SpriteColor((r * n + i) * 2 + 1) & 0xffffff;
                }
                var x = src[n + i].X;
                var y = src[n + i].Y;
                _batch.Add(x, y, x + sizeB, y + sizeB, c | alpha << 24);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // variants 0 and 2: emitter rows

    private Span<Particle> Row(int row) => _particles.AsSpan(row * _pointsPerRow * 2);

    /// <summary>0x451c</summary>
    private void RenderRings(PcmBlock pcm)
    {
        _rows = 50;
        var head = _head + 1;
        _pointsPerRow = 18;
        _head = head;
        if (head > 49) _head = 0;

        var e = _ring;
        e.Count = _pointsPerRow;
        e.Owner = this;
        e.Rows = _rows;
        e.Frame = _frame;
        e.Pulse = _pulse;
        e.Emit(Row(_head), pcm, _head);

        _batch.Reset(0);
        for (var r = 0; r < _rows; r++)
        {
            var age = _head - r;
            if (age < 0) age = age + _rows;
            _ring.Update(Row(r), age, r);
            if (age != 0) _ring.Draw(_batch, Row(r), age, r);
        }
    }

    /// <summary>0x48b4 (Emit gets the pcm and the head row; Update/Draw get age and row)</summary>
    private void RenderClouds(PcmBlock pcm)
    {
        _rows = 50;
        var head = _head + 1;
        _pointsPerRow = 18;
        _head = head;
        if (head > 49) _head = 0;

        var cloud = _cloud;
        var ring = _ring;
        cloud.Count = _pointsPerRow;
        cloud.Owner = this;
        cloud.Rows = _rows;
        ring.Owner = this;
        cloud.Frame = _frame;
        ring.Count = 14; // rings use 2*14 of the row's 36 particle slots
        cloud.Pulse = _pulse;
        ring.Rows = _rows;
        ring.Frame = _frame;
        ring.Pulse = _pulse;

        TrailEmitter e = _ringMode ? ring : cloud;
        _rowEmitter[_head] = e;
        e.Emit(Row(_head), pcm, _head);

        _batch.Reset(0);
        for (var r = 0; r < _rows; r++)
        {
            var owner = _rowEmitter[r];
            var age = _head - r;
            if (age < 0) age = age + _rows;
            if (owner is null) continue;
            owner.Update(Row(r), age, r);
            if (age != 0) owner.Draw(_batch, Row(r), age, r);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // texture generators: w x h RGBA8888 textures in PSP swizzled layout, RGB = white, only alpha varies.

    /// <summary>
    /// Index (in 32-bit texels) of texel (x, y) in a PSP-swizzled 32-bit texture of width w (16-byte x
    /// 8-row blocks), from the byte offset exactly as computed in 0xe24c / 0xe574 (always a multiple of 4).
    /// </summary>
    private static int SwizzledIndex(int x, int y, int w)
    {
        var xbytes = (x * 0x20) / 8; // x * 4
        var offset = ((y / 8) * ((w * 4) / 16) * 8 + (y & 7)) * 0x10 + (xbytes / 16) * 0x80 + (xbytes & 0xf);
        return offset >> 2;
    }

    /// <summary>0xe24c: alpha = 255 * exp(-d^2 / (2 sigma^2)), d = distance from (w/2, h/2).</summary>
    public static void GenerateGaussian(float sigma, uint[] pixels, int w, int h)
    {
        var k = 1.0f / ((sigma + sigma) * sigma);
        for (var y = 0; y < h; y++)
        {
            var dy = y - h / 2;
            for (var x = 0; x < w; x++)
            {
                var dx = x - w / 2;
                var e = Exp(-(float)(dx * dx + dy * dy) * k);
                var a = FloatToU32(e * 255.0f);
                pixels[SwizzledIndex(x, y, w)] = a << 24 | 0xffffff;
            }
        }
    }

    /// <summary>0xe574: alpha = max(255 - (int)((16 |radius - d|)^2), 0): a thin bright ring.</summary>
    public static void GenerateRing(float radius, uint[] pixels, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            var dy = y - h / 2;
            for (var x = 0; x < w; x++)
            {
                var dx = x - w / 2;
                var d = radius - Sqrt((float)(dx * dx + dy * dy));
                if (!(0.0f < d)) d = -d;
                var v = d * 16.0f;
                v = v * v;
                var a = Fmax((float)(0xff - (int)v), 0.0f);
                pixels[SwizzledIndex(x, y, w)] = FloatToU32(a) << 24 | 0xffffff;
            }
        }
    }

    /// <summary>
    /// 0xe790: dst.alpha = t * a.alpha + (1 - t) * b.alpha, dst.rgb = 255 (pixelwise; the layout is the
    /// same for all three, so swizzling does not matter).
    /// </summary>
    public static void BlendAlpha(float t, uint[] dst, uint[] a, uint[] b, int w, int h)
    {
        var u = 1.0f - t;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                // bytes 0..2 = 0xff, byte 3 = (u8) blended alpha
                var alpha = (byte)FloatToU32(t * (float)(int)(a[i] >> 24) + u * (float)(int)(b[i] >> 24));
                dst[i] = (uint)alpha << 24 | 0xffffff;
            }
        }
    }
}
