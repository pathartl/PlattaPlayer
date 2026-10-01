using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PlattaPlayer.Wmp.Harness.Interop;
using PlattaPlayer.Wmp.Harness.Synth;

namespace PlattaPlayer.Wmp.Harness.Verbs;

/// <summary>
/// Ground truth for the one link no other oracle covers: how WMP turns AUDIO into the TimedLevel bytes
/// every effect receives.
///
/// It hosts the real Windows Media Player ActiveX control in this process, plays a synthetic WAV whose
/// content is known exactly (<see cref="TestSignal"/>), and records every TimedLevel WMP hands to its
/// visualization. The recording works like this:
/// <list type="number">
/// <item>wmp.dll's <c>CoCreateInstance</c> import is redirected (<see cref="ImportPatch"/>), so we see
/// the visualization object WMP creates. WMP's own preference picks Alchemy (mpvis.DLL).</item>
/// <item>That object's vtable pointer is swapped for a private copy in which only <c>Render</c> (slot 3)
/// and <c>RenderWindowed</c> (slot 19) are replaced. Every other method stays the real one, and
/// <c>this</c> is still the real object, so no COM identity is faked.</item>
/// <item>The two stubs copy the 0x1010-byte TimedLevel, then call the original.</item>
/// </list>
/// Nothing outside this process is touched. Playback is muted (checked, not assumed: the capture
/// reports whether the spectrum was non-zero).
///
/// Writes <c>artifacts/timedlevel/test.wav</c>, <c>frames.bin</c> (AudioFrameSet) and <c>segments.txt</c>.
///
/// Usage: capture-timedlevel [--unmuted]
/// </summary>
internal static unsafe class CaptureTimedLevel
{
    private static readonly Guid ClsidAlchemy = WmpGuids.ClsidAlchemy;
    private static readonly Guid IidEffects2 = WmpGuids.IWMPEffects2;
    private const string WmpOcxClsid = "6bf52a52-394a-11d3-b153-00c04f79faa6";

    private const int VtableSlots = 24;
    private const int SlotRender = 3;
    private const int SlotRenderWindowed = 19;

    private static delegate* unmanaged<Guid*, nint, uint, Guid*, nint*, int> _coCreate;
    private static delegate* unmanaged<nint, byte*, nint, void*, int> _render;
    private static delegate* unmanaged<nint, byte*, int, int> _renderWindowed;
    private static readonly List<AudioFrame> Frames = [];
    private static readonly List<Guid> Created = [];
    private static nint _instrumented;

    public static string Dir => Path.Combine(HarnessPaths.Root, "timedlevel");

    public static int Run(string[] args)
    {
        var muted = !args.Contains("--unmuted");
        Directory.CreateDirectory(Dir);
        var wav = Path.Combine(Dir, "test.wav");
        TestSignal.Write(wav);
        File.WriteAllLines(Path.Combine(Dir, "segments.txt"), TestSignal.Describe());
        Console.WriteLine($"test signal: {wav} ({TestSignal.Seconds:0.0} s, {TestSignal.Segments.Length} segments)");

        _coCreate = (delegate* unmanaged<Guid*, nint, uint, Guid*, nint*, int>)
            NativeLibrary.GetExport(NativeLibrary.Load("combase.dll"), "CoCreateInstance");

        using var form = new Form { Width = 800, Height = 600, Text = "capture-timedlevel", ShowInTaskbar = false };
        var ax = new WmpAxHost { Dock = DockStyle.Fill };
        form.Controls.Add(ax);
        form.Load += (_, _) =>
        {
            var wmp = NativeMethods.GetModuleHandle("wmp.dll");
            if (wmp == 0) throw new InvalidOperationException("wmp.dll did not load with the control.");
            var hook = (nint)(delegate* unmanaged<Guid*, nint, uint, Guid*, nint*, int>)&CoCreateHook;
            if (!ImportPatch.Redirect(wmp, "api-ms-win-core-com-l1-1-0.dll", "CoCreateInstance", hook))
                throw new InvalidOperationException("wmp.dll does not import CoCreateInstance where expected.");

            dynamic ocx = ax.GetOcxObject();
            ocx.uiMode = "full";
            ocx.settings.mute = muted;
            ocx.URL = wav;
        };

        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        var started = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - started).TotalSeconds > TestSignal.Seconds + 4) form.Close();
        };
        timer.Start();
        Application.Run(form);
        timer.Stop();
        ImportPatch.RestoreAll();

        Console.WriteLine($"CoCreateInstance saw: {string.Join(", ", Created.Distinct())}");
        Console.WriteLine($"instrumented effect: {(_instrumented != 0 ? $"0x{_instrumented:X}" : "NONE")}");
        var fresh = Frames.Where(f => f.State == 2).ToList();
        var nonZero = fresh.Count(f => f.Frequency0.Any(b => b != 0));
        Console.WriteLine($"captured {Frames.Count} TimedLevels ({fresh.Count} fresh, {nonZero} with a non-zero spectrum)");
        if (Frames.Count == 0) return 1;

        AudioFrameSet.Write(Path.Combine(Dir, "frames.bin"), Frames);
        Console.WriteLine($"wrote {Path.Combine(Dir, "frames.bin")}");
        return nonZero > 0 ? 0 : 1;
    }

    [UnmanagedCallersOnly]
    private static int CoCreateHook(Guid* clsid, nint outer, uint context, Guid* iid, nint* ppv)
    {
        var hr = _coCreate(clsid, outer, context, iid, ppv);
        try
        {
            Created.Add(*clsid);
            if (hr >= 0 && *clsid == ClsidAlchemy && *ppv != 0 && _instrumented == 0) Instrument(*ppv);
        }
        catch
        {
            // Never unwind into native code.
        }
        return hr;
    }

    /// <summary>
    /// Swap the effect object's vtable for a copy with recording stubs in the two render slots. The
    /// object is reached through IWMPEffects2, which is what the host renders through.
    /// </summary>
    private static void Instrument(nint unknown)
    {
        nint obj;
        var iid = IidEffects2;
        var qi = (*(delegate* unmanaged<nint, Guid*, nint*, int>**)unknown)[0];
        if (qi(unknown, &iid, &obj) < 0 || obj == 0) return;

        var vtable = (nint*)NativeMemory.Alloc(VtableSlots * (nuint)sizeof(nint));
        Buffer.MemoryCopy(*(nint**)obj, vtable, VtableSlots * sizeof(nint), VtableSlots * sizeof(nint));
        _render = (delegate* unmanaged<nint, byte*, nint, void*, int>)vtable[SlotRender];
        _renderWindowed = (delegate* unmanaged<nint, byte*, int, int>)vtable[SlotRenderWindowed];
        vtable[SlotRender] = (nint)(delegate* unmanaged<nint, byte*, nint, void*, int>)&RenderHook;
        vtable[SlotRenderWindowed] = (nint)(delegate* unmanaged<nint, byte*, int, int>)&RenderWindowedHook;
        *(nint**)obj = vtable;
        _instrumented = obj;

        // Balance the QI; the host holds its own reference.
        var release = (*(delegate* unmanaged<nint, uint>**)obj)[2];
        release(obj);
    }

    [UnmanagedCallersOnly]
    private static int RenderHook(nint self, byte* level, nint hdc, void* rect)
    {
        Record(level);
        return _render(self, level, hdc, rect);
    }

    [UnmanagedCallersOnly]
    private static int RenderWindowedHook(nint self, byte* level, int required)
    {
        Record(level);
        return _renderWindowed(self, level, required);
    }

    private static void Record(byte* level)
    {
        if (level == null) return;
        var f = new AudioFrame();
        new ReadOnlySpan<byte>(level, 0x400).CopyTo(f.Frequency0);
        new ReadOnlySpan<byte>(level + 0x400, 0x400).CopyTo(f.Frequency1);
        new ReadOnlySpan<byte>(level + 0x800, 0x400).CopyTo(f.Waveform0);
        new ReadOnlySpan<byte>(level + 0xC00, 0x400).CopyTo(f.Waveform1);
        f.State = *(int*)(level + 0x1000);
        f.TimeStamp = *(long*)(level + 0x1008);
        lock (Frames) Frames.Add(f);
    }

    /// <summary>The WMP ActiveX control, hosted by WinForms.</summary>
    private sealed class WmpAxHost() : AxHost(WmpOcxClsid)
    {
        public object GetOcxObject() => GetOcx()!;
    }
}

/// <summary>
/// The capture's test signal: 16-bit stereo 44.1 kHz, a sequence of segments whose spectra are known in
/// closed form, so WMP's encoding can be read off directly: silence, a 1 kHz sine at 0 / -20 / -40 dBFS
/// (the slope and reference), bass and treble sines (where Alchemy reads), white noise (energy vs.
/// magnitude), and a left-only and a right-only sine (the channel mapping).
/// </summary>
internal static class TestSignal
{
    public const int Rate = 44100;
    public const double SegmentSeconds = 3.0;

    public static readonly (string Name, double Hz, double Dbfs, bool Left, bool Right, bool Noise)[] Segments =
    [
        ("silence", 0, 0, true, true, false),
        ("1k 0dB", 1000, 0, true, true, false),
        ("1k -20dB", 1000, -20, true, true, false),
        ("1k -40dB", 1000, -40, true, true, false),
        ("100Hz -12dB", 100, -12, true, true, false),
        ("60Hz -12dB", 60, -12, true, true, false),
        ("10k -12dB", 10000, -12, true, true, false),
        ("noise -20dB", 0, -20, true, true, true),
        ("1k -12dB L", 1000, -12, true, false, false),
        ("1k -12dB R", 1000, -12, false, true, false),
        ("silence", 0, 0, true, true, false),
    ];

    public static double Seconds => Segments.Length * SegmentSeconds;

    public static IEnumerable<string> Describe() =>
        Segments.Select((s, i) => $"{i}\t{i * SegmentSeconds:0.0}s\t{s.Name}");

    public static void Write(string path)
    {
        var perSegment = (int)(Rate * SegmentSeconds);
        var total = perSegment * Segments.Length;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF"u8);
        bw.Write(36 + total * 4);
        bw.Write("WAVEfmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)2);
        bw.Write(Rate);
        bw.Write(Rate * 4);
        bw.Write((short)4);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(total * 4);

        var noise = new Random(1);
        for (var s = 0; s < Segments.Length; s++)
        {
            var (_, hz, dbfs, left, right, isNoise) = Segments[s];
            var amplitude = hz > 0 || isNoise ? 32767.0 * Math.Pow(10, dbfs / 20) : 0.0;
            for (var n = 0; n < perSegment; n++)
            {
                double v;
                if (isNoise) v = amplitude * Math.Sqrt(3) * (noise.NextDouble() * 2 - 1); // RMS = amplitude
                else v = amplitude * Math.Sin(2 * Math.PI * hz * n / Rate);
                var sample = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
                bw.Write(left ? sample : (short)0);
                bw.Write(right ? sample : (short)0);
            }
        }
    }
}
