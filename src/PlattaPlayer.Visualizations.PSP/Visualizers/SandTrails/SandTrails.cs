using System.Runtime.InteropServices;
using PlattaPlayer.Visualizations.PSP.Common;
using PlattaPlayer.Visualizations.PSP.Gu;
using PlattaPlayer.Visualizations.PSP.Visualizers.Sand;
using static PlattaPlayer.Visualizations.PSP.Common.VisMath;
using static PlattaPlayer.Visualizations.PSP.Gu.GuConstants;

namespace PlattaPlayer.Visualizations.PSP.Visualizers;

/// <summary>
/// Visualizer type 7, "sand trails" (src/vis/sand_trails/; thumbnail music_tex_vis_thum_6: tan ground with
/// thin dark trails). Ctor 0x8238, object size 0x8070, vtables 0x148b8 (Visualizer) and 0x148fc (embedded
/// thread object at +0x28).
///
/// <para>The 480x272 image is computed on the CPU:</para>
/// <list type="number">
/// <item><b>Height map</b>, one byte per pixel. On the first drawn frame the Earth scheme fills it with a
/// noisy vertical ramp (values about 16..61); a copy is kept as the backup.</item>
/// <item><b>Palette</b>: 256 entries from the Earth scheme (0x10a64), an HSV gradient pale grey -> olive/brown
/// -> dark. Low heights are tan/pink-beige, high ones olive/dark brown.</item>
/// <item><b>Walkers</b> (3 pens, 0x11de4), each with a position, a velocity and 15 oscillators. Every step each
/// oscillator adds +1 (saturating) to the height map at <c>pen + amp * (PolyCos(t*fx), PolySin(t*fy))</c>,
/// <c>amp = A * ((S * (Sin(t*f)+1.5)) * 0.5)</c>: spirograph loops that darken over time. Every 60 steps
/// the velocity is steered; otherwise friction and move, and a pen leaving the screen jumps to a random spot.</item>
/// <item><b>Audio</b>: only <c>levelDb</c> of LevelMeter(-40 dB, 0.3 s, mono). <c>d = levelDb + 40</c>; active
/// walkers = min((int)(d*0.45)+1, 3), steps per walker = (int)(d*0.035)+1.</item>
/// <item><b>Re-colouring</b> (0x117e4): 512 random pixels per frame are recomputed from the palette with a
/// separable 7x7 Gaussian whose sigma grows with the squared distance from (440,40), plus an additive glow
/// <c>max(0, 15 - r²)</c> around (160,250) times <c>light</c>. The first frame repaints everything without
/// glow (0x115b8). New trails therefore "develop" gradually.</item>
/// <item><b>Ramp</b>: <c>level = min(frameCount * 7e-6, 0.15)</c>; <c>light = (9, 7.5, 5) * level</c>,
/// <c>blurScale = min(level/2, 0.5)</c>.</item>
/// <item><b>Reset cycle</b>: every 20146 drawn frames the height map is restored from the backup for 45
/// frames; the random re-colouring then slowly erases the trails from the picture.</item>
/// <item><b>Display</b>: sceGuCopyImage into a locked 480x273 8888 paf surface, drawn as a flat 4x4 Bezier
/// patch scaled to 480x272 (564.7x320 wide) with <c>sceGuColor(alpha&lt;&lt;24|0xffffff)</c>, alpha =
/// brightness*255*fade/48 (48-frame fade-in once the first image exists).</item>
/// </list>
/// <para>Dead parts reproduced for rand()/fidelity: the 240x136 <see cref="RandomField"/> (32640 rand() calls
/// in the ctor, never read); the embedded JPEG decoded into <see cref="_jpegPixels"/> and passed to the
/// canvas, which never reads it; the canvas Gaussian tables; the time-of-day scheme table (only slot 1,
/// Earth, is ever selected, and only its light is used); the message fields ampAvg3/stereoDiff; the
/// level history ring.</para>
/// <para>Threading: on the PSP a kernel thread ("VisThread", body 0x89a8) computes the image, fed through a
/// mailbox and an event flag: Render polls the flag; if the thread is idle it copies the last image to
/// the texture and sends a message. This port is the decompilation's default synchronous mode:
/// <see cref="ThreadSetup"/> runs at the end of the ctor (where the thread was started),
/// <see cref="ThreadStep"/> runs inside Render where the message is sent (so each Render shows the image of
/// the previous step, then computes the next one: what the PSP does whenever the thread keeps up), and
/// <see cref="ThreadTeardown"/> runs in Dispose. The delay/RTC/system-time calls are dropped.</para>
/// <para>rand() order: ctor 240*136 (RandomField), setup 45*4 (waves) + 3*9 (walkers); first drawn frame
/// 480*272 (FillHeightmap); every drawn frame walker respawns (2 each) then 512*2 (RenderRandomPixels; the
/// first frame also does this after RenderAll).</para>
/// <para>skipFrames starts at 0, so the first message is ignored; player event 7 sets it to 30 (the thread
/// ignores the next 31 messages: the picture freezes).</para>
/// </summary>
public sealed class SandTrails : Visualizer
{
    private const int Width = 480;
    private const int Height = 272;

    /// <summary>0x14240: 4x4 Bezier control points {u, v, x, y, z} of a flat quad covering [-0.5, 0.5]^2
    /// (y up), texture 0..1. Scaled by (480|564.7, 272|320, 1).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PatchVertex(float u, float v, float x, float y, float z)
    {
        public readonly float U = u, V = v, X = x, Y = y, Z = z;
    }

    private static readonly PatchVertex[] Patch =
    [
        new(0.00f, 0.00f, -0.50f, 0.50f, 0.0f), new(0.25f, 0.00f, -0.25f, 0.50f, 0.0f),
        new(0.75f, 0.00f, 0.25f, 0.50f, 0.0f), new(1.00f, 0.00f, 0.50f, 0.50f, 0.0f),
        new(0.00f, 0.25f, -0.50f, 0.25f, 0.0f), new(0.25f, 0.25f, -0.25f, 0.25f, 0.0f),
        new(0.75f, 0.25f, 0.25f, 0.25f, 0.0f), new(1.00f, 0.25f, 0.50f, 0.25f, 0.0f),
        new(0.00f, 0.75f, -0.50f, -0.25f, 0.0f), new(0.25f, 0.75f, -0.25f, -0.25f, 0.0f),
        new(0.75f, 0.75f, 0.25f, -0.25f, 0.0f), new(1.00f, 0.75f, 0.50f, -0.25f, 0.0f),
        new(0.00f, 1.00f, -0.50f, -0.50f, 0.0f), new(0.25f, 1.00f, -0.25f, -0.50f, 0.0f),
        new(0.75f, 1.00f, 0.25f, -0.50f, 0.0f), new(1.00f, 1.00f, 0.50f, -0.50f, 0.0f),
    ];

    /// <summary>The message the render side sends to the thread each frame (0x16344..0x16363 in .bss).</summary>
    private sealed class RenderMessage
    {
        // +0x00 SceKernelMsgPacket header (next, priority), +0x08 unused
        public float LevelDb;    // +0x0c  LevelMeter(-40 dB floor, 0.3 s, mono).Db()
        public float AmpAvg3;    // +0x10  mean of the last three ampNow values (never read)
        public float StereoDiff; // +0x14  mean |L-R| of the first 100 frames / 65535 (never used)
        public float AmpPrev;    // +0x18  (0x1635c) previous ampNow
        public float AmpPrev2;   // +0x1c  (0x16360) the one before
    }

    /// <summary>One colour scheme slot (time-of-day table at +0x40, 16 bytes each).</summary>
    private struct SchemeSlot
    {
        public int StartHour;   // +0x0
        public int EndHour;     // +0x4
        public uint[] Palette;  // +0x8
        public Scheme Scheme;   // +0xc
    }

    /// <summary>State that lived on the render thread's 0x4920-byte stack.</summary>
    private sealed class ThreadState(Canvas canvas)
    {
        public readonly Canvas Canvas = canvas;              // sp+0x20
        public readonly Wave[] Waves = new Wave[45];         // sp+0x4160 (3 x 15)
        public Walker[]? Walkers;                            // new[] of 3 pointers (s fp)
        public readonly float[] LevelHistory = new float[100]; // sp+0x4700 ring of received levelDb (write only)
        public int HistoryPos;                               // s3
        public int FrameCount;                               // sp+0x48c0 frames drawn since the thread started
        public int RestoreCounter;                           // sp+0x48c4 0..20145, height map reset window 20101..20145
        public int RestorePhase;                             // sp+0x48c8
        public int PerfCounter;                              // s6 (profiling every 301 frames, results discarded)
    }

    // base Visualizer: +0x04 alpha, +0x10 color[4], +0x20 owner, +0x24 brightness
    // +0x28 thread object: vtable 0x148fc (ctor 0xe884, dtor 0xe8e0), +0x2c "VisThread" id (not ported)
#pragma warning disable CS0414 // write-only fields kept for fidelity
    private byte _threadFlag30;                     // +0x30  cleared by ctor and by player event 7, never read
    private uint[]? _palette;                       // +0x34  palettes block (memalign 0x1800) +0: palette in use
    private uint[]? _unused38;                      // +0x38  block + 0x400 (unused)
    private uint[]? _paletteCopy;                   // +0x3c  block + 0x800 (copy of _palette, unused)
    private readonly SchemeSlot[] _schemes = new SchemeSlot[3]; // +0x40 {6,11}, {11,17}, {17,6}
    private int _current = -1;                      // +0x70  current SchemeSlot* (index)
    private uint[]? _paletteAsh;                    // +0x74  block + 0xc00
    private uint[]? _paletteEarth;                  // +0x78  block + 0x1000
    private uint[]? _paletteSky;                    // +0x7c  block + 0x1400
    private PafSurface? _surface;                   // +0x80  SurfaceRCPtr, 480x273 RGBA8888
    private uint[]? _pixels;                        // +0x84  _surface locked for its whole lifetime (non-null = loaded)
    private readonly RandomField _randomField;      // +0x88  240x136, never read
    private byte[]? _heightMap;                     // +0x98  work buffer + 0        (0xbf400 bytes in total)
    private byte[]? _backup;                        //        work buffer + 0x1fe00
    private uint[]? _image;                         //        work buffer + 0x3fc00 (RGBA image)
    private readonly uint[] _jpegPixels = new uint[0x7f80 / 4]; // +0x9c decoded embedded JPEG (120x68 RGBA), never read
    private bool _running;                          // +0x801c thread keeps going while set
    // +0x8020 / +0x8024 event flag / mailbox ids (not ported)
    private bool _initialized;                      // +0x8028 set by the thread after its first image
    private int _fadeFrames;                        // +0x802c fade-in counter, 0..48
    private int _skipFrames;                        // +0x8030 messages to ignore (event 7 sets 30)
    private readonly LevelMeter _level;             // +0x8034 (0x28 bytes)
    private int _delayUs;                           // +0x805c sceKernelDelayThread argument (Render sets 10)
#pragma warning restore CS0414
    private bool _resourcesOk;                      // +0x8060 allocations / JPEG decode succeeded

    private ThreadState? _thread;                   // port: the render thread's stack frame
    private bool _frameDone;                        // port: RenderingEventFlag bit 0 (created set)

    // Port: the original's message packet (0x16344) and the float at 0x14ae4 are .bss globals shared by
    // all instances; they are per instance here. Nothing observable depends on that (levelDb is written
    // before every send, the rest is never read).
    private readonly RenderMessage _renderMessage = new();
    private float _unused14ae4; // 0x14ae4: written during the height map restore window, read nowhere

    /// <summary>0x8238</summary>
    public SandTrails(PspRuntime runtime) : base(runtime)
    {
        _randomField = new RandomField(runtime, 0xf0, 0x88); // 0xf710: 240*136 rand() calls
        _level = new LevelMeter(-40.0f, 0.3f, true);          // 0xf154

        _threadFlag30 = 0;
        _surface = null; // 0x1031c
        _pixels = null;
        _initialized = false;
        _fadeFrames = 0;
        _skipFrames = 0;
        _resourcesOk = true;
        _delayUs = 0;

        // memalign(0x40, 0xbf400) work buffer and memalign(0x40, 0x1800) palettes block (zeroed here;
        // the original never fails in practice).
        _heightMap = new byte[Width * Height];
        _backup = new byte[Width * Height];
        _image = new uint[Width * Height];
        _palette = new uint[0x100];
        _paletteCopy = new uint[0x100];
        _unused38 = new uint[0x100];
        _paletteAsh = new uint[0x100];
        _paletteEarth = new uint[0x100];
        _paletteSky = new uint[0x100];

        // Decode the embedded JPEG (format 2) and keep 0x7f80 bytes of pixels. The JPEG comes from the
        // user's firmware at runtime; the pixels are never read.
        var img = runtime.OpenJpeg(runtime.Assets.SandTrailsJpeg);
        if (img is null)
        {
            // The original falls through and calls ToBuffer/memcpy on the null image anyway (would
            // crash); like the C++ port, the copy is skipped.
            _resourcesOk = false;
        }
        else
        {
            // (the original also reads img+0x2c / +0x30 (width/height) into unused locals)
            var n = Math.Min(img.Pixels.Length, _jpegPixels.Length);
            img.Pixels.AsSpan(0, n).CopyTo(_jpegPixels);
        }

        // Palettes. The Ash and Earth palettes are built with temporary scheme objects on the stack;
        // the Sky palette loop (0x10fac) is inlined.
        new AshScheme().BuildPalette(_paletteAsh, 0x100);     // 0x111a4
        new EarthScheme().BuildPalette(_paletteEarth, 0x100); // 0x10a64
        new SkyScheme().BuildPalette(_paletteSky, 0x100);     // inlined 0x10fac

        // Time-of-day table. Note the 06..11 slot pairs the Ash palette with a Sand scheme object (as in
        // the binary). Only slot 1 is ever selected (see SelectScheme).
        _schemes[0] = new SchemeSlot { Palette = _paletteAsh, StartHour = 6, EndHour = 11, Scheme = new SandScheme() };     // vtable 0x14970
        _schemes[1] = new SchemeSlot { Palette = _paletteEarth, StartHour = 11, EndHour = 17, Scheme = new EarthScheme() }; // vtable 0x149a0
        _schemes[2] = new SchemeSlot { Palette = _paletteSky, StartHour = 17, EndHour = 6, Scheme = new SkyScheme() };      // vtable 0x14988

        // Synchronous port: mailbox / event flag creation always "succeeds"; the thread's prologue runs
        // here, where the PSP would start the thread.
        _running = true;
        _frameDone = true; // RenderingEventFlag created with bit 0 set
        _initialized = false;
        if (_resourcesOk) ThreadSetup();
    }

    /// <summary>0x8594 (deleting: 0x86e4)</summary>
    public override void Dispose()
    {
        UnloadResources(); // direct call to 0x8900
        _running = false;
        // What the thread does once its ReceiveMbx is cancelled.
        if (_thread is not null) ThreadTeardown();
        _heightMap = null;
        _backup = null;
        _image = null;
        _palette = null;
        _unused38 = null;
        _paletteCopy = null;
        _paletteAsh = null;
        _paletteEarth = null;
        _paletteSky = null;
        base.Dispose();
    }

    /// <summary>0x883c</summary>
    public override void LoadResources()
    {
        if (!IsLoaded() && _resourcesOk)
        {
            var s = new PafSurface(480, 0x111); // paf::CreateSurface(480, 273, 8888, 1, false, 1, 0, 0)
            _surface = s;                       // RCPtr assign 0x10910 (releases any previous surface)
            _pixels = _surface.Lock(0);
            Array.Clear(_pixels, 0, 0x7ff80 / 4); // memset 480 * 273 * 4
        }
    }

    /// <summary>0x8900</summary>
    public override void UnloadResources()
    {
        if (IsLoaded() && _resourcesOk)
        {
            _pixels = null;
            _surface!.Unlock();
            _surface = null; // RCPtr assign(nullptr) 0x10910
        }
    }

    /// <summary>0x896c</summary>
    public override bool IsLoaded() => _pixels is not null;

    /// <summary>0x9580</summary>
    public override void OnPlayerEvent(PlayerEvent ev)
    {
        switch (ev.Type)
        {
            case 7: // meaning of player event 7 unknown (maybe a track change); freezes the thread for 31 messages
                _skipFrames = 30;
                _threadFlag30 = 0;
                break;
        }
    }

    /// <summary>0x91ec</summary>
    public override void Render(PcmBlock pcm)
    {
        var samples = pcm.Samples;
        if (!_resourcesOk) return;

        // fade in over 48 frames after the first image exists
        var fade = _fadeFrames;
        if (fade > 47)
        {
            _fadeFrames = 48;
            fade = 48;
        }
        var a = (Brightness * 255.0f * (float)fade) / 48.0f;
        var alpha = FloatToU32(a) & 0xff; // float -> unsigned
        _delayUs = 10;
        if (_initialized) _fadeFrames = _fadeFrames + 1;

        var ready = _frameDone; // sceKernelPollEventFlag(evf, 1, AND) != EVF_COND
        if (ready)
        {
            _frameDone = false; // sceKernelClearEventFlag(evf, 0)

            // the thread's last finished image -> texture (GE transfer).
            // Port: the controller only calls Render when IsLoaded(); the copy is skipped otherwise
            // (the original would write through a null pointer).
            if (_pixels is not null)
                Gu.CopyImage(GU_PSM_8888, 0, 0, 480, 272, 480, _image, 0, 0, 480, _pixels);

            // audio features of the first 100 stereo frames
            var sumDiff = 0;
            var p = 0;
            for (var i = 99; i >= 0; --i, p += 2)
            {
                var d = (int)samples[p] - (int)samples[p + 1];
                sumDiff += (d <= 0) ? (int)samples[p + 1] - (int)samples[p] : d;
            }
            var sumAmp = 0;
            p = 0;
            for (var i = 99; i >= 0; --i, p += 2)
            {
                int l = samples[p];
                if (l < -l) l = -l;          // max(l, -l)
                sumAmp += l + samples[p + 1]; // NOTE: right channel is added signed, not abs
            }
            var msg = _renderMessage;
            msg.StereoDiff = ((float)sumDiff / 100.0f) / 65535.0f;
            var ampNow = ((float)sumAmp / 100.0f) / 65535.0f;
            _level.Update(pcm);        // 0xf1cc
            msg.LevelDb = _level.Db(); // 0xf4c0
            var prev = msg.AmpPrev;
            var prev2 = msg.AmpPrev2;
            msg.AmpPrev2 = prev;
            msg.AmpPrev = ampNow;
            msg.AmpAvg3 = ((prev + prev2) + ampNow) / 3.0f;

            // SYNCHRONOUS PORT of sceKernelSendMbx: the render thread receives the message and runs one
            // iteration right away (on the PSP it runs concurrently and sets the flag when done).
            if (_running && _thread is not null) ThreadStep(msg);
        }

        if (_surface is null) return; // port: see the CopyImage note above

        // draw the texture as a full-screen Bezier patch
        // (a translate vector {-240, -136, 0} is stored on the stack but never used)
        float scaleX, scaleY;
        if (!Runtime.IsWideOutput)
        {
            scaleX = 480.0f;
            scaleY = 272.0f;
        }
        else
        {
            scaleX = BitsFloat(0x440d2d2d); // 564.7059
            scaleY = 320.0f;
        }
        // sceKernelDcacheWritebackAll() dropped (no cache to write back)
        Gu.Color(alpha << 24 | 0xffffff);
        Gu.Enable(GU_TEXTURE_2D);
        Gu.SetTexture(_surface, 0.0f, 0.0f, 1.0f, 1.0f);
        Gu.GumPushMatrix();
        Gu.GumLoadIdentity();
        Gu.GumScale(scaleX, scaleY, 1.0f);
        // Surface +0x18 / +0x1a (shorts) = width / height -> 480x273 gives 8 x 5
        Gu.PatchDivide((uint)((short)_surface.Width + 0x3f) >> 6, (uint)((short)_surface.Height + 0x3f) >> 6);
        Gu.PatchPrim(GU_TRIANGLE_STRIP);
        Gu.Enable(GU_TEXTURE_2D);
        Gu.GumDrawBezier(GU_TEXTURE_32BITF | GU_VERTEX_32BITF, 4, 4, (ReadOnlySpan<PatchVertex>)Patch); // vtype 0x183
        Gu.GumPopMatrix();
    }

    /// <summary>
    /// 0x8978: called every drawn frame. Reads the local time (sceRtcGetCurrentClockLocalTime, omitted)
    /// and then ignores it: the compiled code always selects slot 1 (11..17 h, Earth).
    /// </summary>
    private void SelectScheme() => _current = 1;

    /// <summary>sceKernelSetEventFlag(evf, 1)</summary>
    private void SignalFrameDone() => _frameDone = true;

    /// <summary>0x89a8 prologue: canvas, waves, walkers, cleared image and height map.</summary>
    private void ThreadSetup()
    {
        if (!_resourcesOk) return;
        Array.Clear(_image!); // memset(buf + 0x3fc00, 0, 0x7f800)
        var d = new CanvasDesc
        {
            HeightMap = _heightMap!,
            Rgba = _image!,
            Backup = _backup!,
            Width = 480,
            Height = 0x110,
            BytesPerPixel = 1,
        };
        var t = _thread = new ThreadState(new Canvas(d, Runtime)); // Canvas ctor 0x113f4

        // 45 oscillators
        for (var k = 0; k < t.Waves.Length; k++)
        {
            ref var w = ref t.Waves[k];
            w.T = 0;
            w.Amp = ((float)Runtime.Rand() / 32767.0f) * 30.0f + 45.0f;
            w.AmpFreq = ((float)Runtime.Rand() / 32767.0f) * 0.01f + 0.001f;
            var f = ((float)Runtime.Rand() / 32767.0f) * 0.01f + 0.001f;
            w.FreqY = f;
            w.FreqX = f;
            var s = (float)Runtime.Rand() / 32767.0f;
            w.AmpScale = (s + s) + 0.001f;
        }

        // 3 walkers with 15 oscillators each
        t.Walkers = new Walker[3];
        for (var k = 0; k < 3; k++)
        {
            var w = new Walker { Waves = t.Waves, WaveBase = k * 15, WaveCount = 15 };
            w.Margin = Runtime.Rand() % 80 + 30;
            w.X = (float)(w.Margin + Runtime.Rand() % 480);
            var ry = Runtime.Rand();
            w.Vx = 0.0f;
            w.Vy = 0.0f;
            w.Y = (float)(w.Margin + ry % 272);
            var u = ((float)Runtime.Rand() / 32767.0f) * 3.0f;
            w.Unused30[0] = u;
            w.Unused30[2] = u;
            w.Unused30[1] = u;
            w.Unused24[0] = ((float)Runtime.Rand() / 32767.0f) * 0.01f + 0.001f;
            w.Unused24[1] = ((float)Runtime.Rand() / 32767.0f) * 0.01f + 0.001f;
            w.Unused24[2] = ((float)Runtime.Rand() / 32767.0f) * 0.01f + 0.001f;
            w.Phase = Runtime.Rand();
            t.Walkers[k] = w;
            w.Counter = 0;
        }

        t.RestoreCounter = 0;
        t.RestorePhase = 0;
        Array.Clear(d.HeightMap, 0, d.BytesPerPixel * d.Width * d.Height);
        t.FrameCount = 0;
        // sceKernelGetSystemTimeLow() (profiling, result discarded) dropped
        Array.Clear(t.LevelHistory);
        t.HistoryPos = 0;
        t.PerfCounter = 0;
    }

    /// <summary>0x89a8 loop body: one received message.</summary>
    private void ThreadStep(RenderMessage msg)
    {
        var t = _thread!;
        var canvas = t.Canvas;
        ref readonly var d = ref canvas.Desc;

        t.LevelHistory[t.HistoryPos] = msg.LevelDb;
        t.HistoryPos = t.HistoryPos + 1;
        if (!(t.HistoryPos < 100)) t.HistoryPos = 0;

        if (_skipFrames >= 0)
        {
            // ignoring messages (initially 1, after player event 7: 31)
            _skipFrames = _skipFrames - 1;
            t.PerfCounter++;
            SignalFrameDone();
            return;
        }

        // every 20146 frames: for 45 frames the height map is reset to its initial noise
        // (trails vanish while the random re-colouring catches up)
        if (t.RestoreCounter > 20100)
        {
            if (t.RestoreCounter < 20146)
            {
                t.RestorePhase = t.RestorePhase + 1;
                var s = Sin((float)t.RestorePhase * 0.001f);
                _unused14ae4 = (s + 1.2f) * 0.015f;
                Array.Copy(d.Backup, d.HeightMap, d.Width * d.Height);
            }
            else
            {
                t.RestoreCounter = 0;
            }
        }
        SelectScheme();
        t.RestoreCounter = t.RestoreCounter + 1;
        // sceKernelDelayThread(_delayUs) dropped

        if (!_initialized)
        {
            // first image: Earth palette + noise height map, full repaint without glow
            var earth = new EarthScheme();
            earth.BuildPalette(_palette!, 0x100); // 0x10a64
            Array.Copy(_palette!, _paletteCopy!, 0x100);
            canvas.Light[0] = 0.0f;
            canvas.Light[1] = 0.0f;
            canvas.Light[2] = 0.0f;
            earth.FillHeightmap(Runtime, d.HeightMap, d.Width, d.Height); // vtable slot 1
            Array.Copy(d.HeightMap, d.Backup, d.Width * d.Height);
            Array.Copy(d.Backup, d.HeightMap, d.Width * d.Height);
            canvas.RenderAll(_palette!, _jpegPixels); // 0x115b8
            _initialized = true;
        }
        else
        {
            // light and blur ramp up over ~21400 frames
            var level = (float)t.FrameCount * BitsFloat(0x36eae18b);              // 7e-6
            if (BitsFloat(0x3e19999a) <= level) level = BitsFloat(0x3e19999a);    // 0.15
            _schemes[_current].Scheme.GetLight(out var r, out var g, out var b);
            var blur = level * 0.5f;
            canvas.Light[0] = r * level;
            canvas.Light[1] = g * level;
            canvas.Light[2] = b * level;
            canvas.LightLevel = level;
            if (0.5f <= blur) blur = 0.5f;
            canvas.BlurScale = blur;

            // loudness -> number of active walkers (1..3) and steps per walker
            var db = msg.LevelDb - -40.0f;
            var info = new WalkInfo { StereoDiff = msg.StereoDiff, LevelDb = msg.LevelDb };
            var walkers = (int)(db * BitsFloat(0x3ee66666)) + 1; // 0.45
            info.Steps = (int)(db * BitsFloat(0x3d0f5c29)) + 1;  // 0.035
            if (walkers > 3) walkers = 3;
            for (var k = 0; k < walkers; k++) t.Walkers![k].Update(canvas, ref t.FrameCount, info, Runtime);
        }

        t.FrameCount = t.FrameCount + 1;
        canvas.RenderRandomPixels(_palette!, _jpegPixels); // 0x117e4
        t.PerfCounter++;
        SignalFrameDone();
        if (t.PerfCounter >= 0x12d)
        {
            t.PerfCounter = 0;
            // 2x sceKernelGetSystemTimeLow() (profiling, results discarded) dropped
        }
    }

    /// <summary>0x89a8 epilogue: frees the walkers (waves and canvas have trivial destructors).</summary>
    private void ThreadTeardown()
    {
        if (_thread is null) return;
        _thread.Walkers = null;
        _thread = null;
    }
}
