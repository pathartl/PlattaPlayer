using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlattaPlayer.Codecs.Midi.Winmm;

/// <summary>
/// The Windows MIDI outputs: the system default synth (the MIDI mapper) plus every <c>midiOut</c> port the OS
/// reports.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinmmMidiDevices
{
    public static IReadOnlyList<MidiDevice> GetDevices()
    {
        var devices = new List<MidiDevice>
        {
            // The mapper routes to whatever the user/OS has set as the default device.
            new("system", "System MIDI (default)", MidiDeviceKind.System, WinmmDeviceId: -1)
        };

        uint count;
        try { count = WinmmMidiInterop.midiOutGetNumDevs(); }
        catch (DllNotFoundException) { return devices; }

        for (var i = 0u; i < count; i++)
        {
            var caps = default(WinmmMidiInterop.MidiOutCaps);
            var result = WinmmMidiInterop.midiOutGetDevCaps((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<WinmmMidiInterop.MidiOutCaps>());
            if (result != 0) continue;

            var name = string.IsNullOrWhiteSpace(caps.szPname) ? $"MIDI device {i}" : caps.szPname;
            devices.Add(new MidiDevice(
                Id: "midiout:" + i,
                Name: name,
                Kind: MidiDeviceKind.External,
                WinmmDeviceId: (int)i));
        }

        return devices;
    }
}
