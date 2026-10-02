namespace PlattaPlayer.Codecs.Midi.Emulation;

/// <summary>
/// One booted 88emu device. Booting runs the firmware for emulated seconds, so a synth is kept across
/// tracks and only replaced when a different model is selected. A context is not thread-safe: every
/// native call goes through <see cref="Gate"/>, and the stream currently allowed to drive the synth is
/// recorded in <see cref="Owner"/> so a stream being torn down can no longer reach it.
/// </summary>
internal sealed class Emu88Synth : IDisposable
{
    private IntPtr _context;

    private Emu88Synth(IntPtr context, int deviceId, int sampleRate)
    {
        _context = context;
        DeviceId = deviceId;
        SampleRate = sampleRate;
    }

    public Lock Gate { get; } = new();

    public int DeviceId { get; }

    /// <summary>The rate <see cref="Render"/> produces: the device's own DAC rate (BASS resamples).</summary>
    public int SampleRate { get; }

    /// <summary>The stream driving the synth; set and read under <see cref="Gate"/>.</summary>
    public object? Owner { get; set; }

    public bool IsOpen => _context != IntPtr.Zero;

    /// <summary>Builds and boots <paramref name="deviceId"/>. Blocks while the firmware boots; null on failure.</summary>
    public static Emu88Synth? Open(int deviceId)
    {
        if (!Emu88Library.IsAvailable) return null;

        var context = Emu88Native.CreateContext();
        if (context == IntPtr.Zero) return null;

        Emu88Native.SetBootFlags(context, Emu88Native.BootDefault);
        Emu88Native.SetStereoOutputSamplerate(context, 0);
        if (Emu88Native.SelectDevice(context, deviceId) != Emu88Native.RcOk
            || Emu88Native.OpenSynth(context) != Emu88Native.RcOk)
        {
            Emu88Native.FreeContext(context);
            return null;
        }

        var rate = (int)Emu88Native.GetActualStereoOutputSamplerate(context);
        if (rate <= 0)
        {
            Emu88Native.FreeContext(context);
            return null;
        }

        return new Emu88Synth(context, deviceId, rate);
    }

    // The members below require Gate to be held.

    public void PlayMessage(uint msg)
    {
        if (IsOpen) Emu88Native.PlayMsg(_context, msg);
    }

    public void PlaySysEx(byte[] sysex)
    {
        if (IsOpen) Emu88Native.PlaySysex(_context, sysex, (uint)sysex.Length);
    }

    /// <summary>The device's own reset (GS Reset, or the MT-32 family's all-parameters reset).</summary>
    public void PlayDeviceReset()
    {
        if (IsOpen) Emu88Native.PlayDeviceReset(_context);
    }

    /// <summary>All Sound Off (or its MT-32-era equivalent) on every channel.</summary>
    public void PlaySilence()
    {
        if (IsOpen) Emu88Native.PlaySilence(_context);
    }

    /// <summary>Renders interleaved stereo float frames; silence when closed.</summary>
    public unsafe void Render(Span<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        if (frames == 0) return;
        if (!IsOpen)
        {
            interleaved.Clear();
            return;
        }

        fixed (float* p = interleaved)
            Emu88Native.RenderFloat(_context, p, (uint)frames);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_context == IntPtr.Zero) return;
            Emu88Native.FreeContext(_context);
            _context = IntPtr.Zero;
            Owner = null;
        }
    }
}
