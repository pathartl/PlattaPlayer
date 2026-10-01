using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// The 0x1010-byte <c>TimedLevel</c> block WMP hands a visualization each frame. Deliberately a raw
/// pinned byte buffer written through named offsets rather than a struct — the layout is then
/// self-documenting and impossible to get subtly wrong through marshalling.
///
/// Layout (confirmed from wmp.dll's filler FUN_180257380, which memcpy's both 0x800 blocks wholesale):
///   +0x000  BYTE frequency[0][1024]
///   +0x400  BYTE frequency[1][1024]
///   +0x800  BYTE waveform [0][1024]
///   +0xC00  BYTE waveform [1][1024]
///   +0x1000 int   state          0 = stopped/silence (both blocks zeroed by the host)
///                                1 = paused  (reuse previous)
///                                2 = playing (fresh data)
///   +0x1008 INT64 timeStamp      100 ns units
/// </summary>
internal sealed class TimedLevelBuffer : IDisposable
{
    public const int FreqCh0 = 0x000;
    public const int FreqCh1 = 0x400;
    public const int WaveCh0 = 0x800;
    public const int WaveCh1 = 0xC00;
    public const int StateOffset = 0x1000;
    public const int TimeStampOffset = 0x1008;
    public const int Size = 0x1010;
    public const int Bins = 1024;

    private readonly byte[] _bytes = new byte[Size];
    private GCHandle _pin;

    public TimedLevelBuffer() => _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);

    public unsafe void* Pointer => (void*)_pin.AddrOfPinnedObject();

    public Span<byte> Frequency0 => _bytes.AsSpan(FreqCh0, Bins);
    public Span<byte> Frequency1 => _bytes.AsSpan(FreqCh1, Bins);
    public Span<byte> Waveform0 => _bytes.AsSpan(WaveCh0, Bins);
    public Span<byte> Waveform1 => _bytes.AsSpan(WaveCh1, Bins);

    public int State
    {
        get => BitConverter.ToInt32(_bytes, StateOffset);
        set => BitConverter.TryWriteBytes(_bytes.AsSpan(StateOffset, 4), value);
    }

    public long TimeStamp
    {
        get => BitConverter.ToInt64(_bytes, TimeStampOffset);
        set => BitConverter.TryWriteBytes(_bytes.AsSpan(TimeStampOffset, 8), value);
    }

    public void Clear() => Array.Clear(_bytes);

    public void Dispose()
    {
        if (_pin.IsAllocated) _pin.Free();
    }
}
