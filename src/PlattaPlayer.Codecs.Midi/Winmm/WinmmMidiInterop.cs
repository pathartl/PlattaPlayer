using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlattaPlayer.Codecs.Midi.Winmm;

/// <summary>
/// Thin P/Invoke layer over the Windows multimedia (<c>winmm.dll</c>) MIDI-output API used to stream
/// live MIDI events to an OS/hardware port.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinmmMidiInterop
{
    /// <summary>The MIDI mapper — the system default MIDI output device.</summary>
    public const uint MidiMapper = 0xFFFFFFFF; // (uint)(-1)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MidiOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")]
    public static extern uint midiOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint midiOutGetDevCaps(UIntPtr uDeviceID, ref MidiOutCaps lpCaps, uint cbSize);

    [DllImport("winmm.dll")]
    public static extern uint midiOutOpen(out IntPtr lphmo, uint uDeviceID, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll")]
    public static extern uint midiOutClose(IntPtr hmo);

    [DllImport("winmm.dll")]
    public static extern uint midiOutShortMsg(IntPtr hmo, uint dwMsg);

    [DllImport("winmm.dll")]
    public static extern uint midiOutReset(IntPtr hmo);

    [DllImport("winmm.dll")]
    public static extern uint midiOutSetVolume(IntPtr hmo, uint dwVolume);
}
