using System.Runtime.InteropServices;

namespace PlattaPlayer.Wmp.Harness.Interop;

/// <summary>
/// The REAL "WMPlayer Spectrum Analyzer DMO" (<c>WMPEffects::CDMOSA</c> in wmpeffects.dll), driven
/// in-process: the code that turns PCM into the TimedLevel every WMP visualization receives.
///
/// Addresses come from Microsoft's public wmpeffects.pdb, so every one is a named function:
/// <list type="bullet">
/// <item><c>CDMOSA::CDMOSA</c> (<c>0x18001f3f0</c>) builds the object (0x11d8 bytes).</item>
/// <item><c>+0x11c8</c> points at the input <c>WAVEFORMATEX</c>, normally set by SetInputType; we set it.</item>
/// <item><c>AllocateStreamingResources</c> (<c>0x18001f720</c>) initializes the FFT (2048 points,
/// Blackman-Harris) and allocates the buffers.</item>
/// <item><c>ProcessNbit(int bytes, byte* pcm)</c> (<c>0x180020450</c>) buffers PCM and, every
/// <c>rate/30</c> samples, runs <c>ComputeFrequenciesFl</c> per channel over the last 2048 and queues a
/// <c>CTimedLevel</c> node (0x1820 bytes) at <c>+0x88</c>, linked through <c>+0x1818</c>.</item>
/// </list>
/// Node layout: spectrum ch0 <c>+0</c> / ch1 <c>+0x400</c>, waveform ch0 <c>+0x800</c> / ch1
/// <c>+0xc00</c>, stream time <c>+0x1010</c>, and a second spectrum (not shifted by the sub-20 Hz bins)
/// at <c>+0x1018</c> / <c>+0x1418</c>.
///
/// Nothing is ever freed through the DLL; the object and its nodes are leaked on purpose.
/// </summary>
internal sealed unsafe class SpectrumAnalyzerOracle
{
    private const long ImageBase = 0x180000000;
    private const long CtorVa = 0x18001f3f0;
    private const long AllocateVa = 0x18001f720;
    private const long ProcessNbitVa = 0x180020450;
    public const long ExponentTableVa = 0x18003a200;
    public const long MantissaTableVa = 0x18003a600;

    private const int ObjectSize = 0x1200;
    private const int NodeNext = 0x1818;

    private static nint _module;
    private readonly byte* _obj;
    private nint _cursor;

    public static nint Module
    {
        get
        {
            if (_module == 0)
            {
                _module = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "wmpeffects.dll"));
                var size = new FileInfo(Path.Combine(Environment.SystemDirectory, "wmpeffects.dll")).Length;
                if (size != 315592)
                    throw new InvalidOperationException(
                        $"wmpeffects.dll is {size} bytes, not the 315592-byte build whose PDB these addresses come from.");
            }
            return _module;
        }
    }

    public static nint At(long va) => Module + (nint)(va - ImageBase);

    public SpectrumAnalyzerOracle(int sampleRate, int channels, int bitsPerSample = 16)
    {
        _obj = (byte*)NativeMemory.AllocZeroed(ObjectSize);
        ((delegate* unmanaged<void*, void*>)At(CtorVa))(_obj);

        var wfx = (byte*)NativeMemory.AllocZeroed(0x20);
        var block = channels * bitsPerSample / 8;
        *(ushort*)(wfx + 0x00) = 1;                      // WAVE_FORMAT_PCM
        *(ushort*)(wfx + 0x02) = (ushort)channels;
        *(uint*)(wfx + 0x04) = (uint)sampleRate;
        *(uint*)(wfx + 0x08) = (uint)(sampleRate * block);
        *(ushort*)(wfx + 0x0c) = (ushort)block;
        *(ushort*)(wfx + 0x0e) = (ushort)bitsPerSample;
        *(byte**)(_obj + 0x11c8) = wfx;
        // What SetInputType (0x180020bf0) does besides storing the format: input wider than 16 bits
        // selects the DOUBLE-precision path, which has no sub-20 Hz trim. WMP's pipeline takes it.
        _obj[0x1140] = bitsPerSample > 16 ? (byte)1 : (byte)0;

        var hr = ((delegate* unmanaged<void*, int>)At(AllocateVa))(_obj);
        if (hr < 0) throw new InvalidOperationException($"AllocateStreamingResources failed: 0x{hr:X8}");
    }

    /// <summary>Feeds interleaved PCM. Nodes become available through <see cref="TakeNew"/>.</summary>
    public void Process(ReadOnlySpan<byte> pcm)
    {
        fixed (byte* p = pcm)
        {
            var hr = ((delegate* unmanaged<void*, int, byte*, int>)At(ProcessNbitVa))(_obj, pcm.Length, p);
            if (hr < 0) throw new InvalidOperationException($"ProcessNbit failed: 0x{hr:X8}");
        }
    }

    /// <summary>A queued TimedLevel, copied out.</summary>
    public sealed record Node(long Time, byte[] Spectrum0, byte[] Spectrum1, byte[] Wave0, byte[] Wave1,
                              byte[] Unshifted0, byte[] Unshifted1);

    /// <summary>Every node queued since the last call, oldest first.</summary>
    public List<Node> TakeNew()
    {
        var result = new List<Node>();
        var node = _cursor == 0 ? *(nint*)(_obj + 0x88) : *(nint*)(_cursor + NodeNext);
        while (node != 0)
        {
            var b = (byte*)node;
            result.Add(new Node(
                *(long*)(b + 0x1010),
                new ReadOnlySpan<byte>(b, 0x400).ToArray(),
                new ReadOnlySpan<byte>(b + 0x400, 0x400).ToArray(),
                new ReadOnlySpan<byte>(b + 0x800, 0x400).ToArray(),
                new ReadOnlySpan<byte>(b + 0xc00, 0x400).ToArray(),
                new ReadOnlySpan<byte>(b + 0x1018, 0x400).ToArray(),
                new ReadOnlySpan<byte>(b + 0x1418, 0x400).ToArray()));
            _cursor = node;
            node = *(nint*)(node + NodeNext);
        }
        return result;
    }

    private const long ComputeFrequenciesDblVa = 0x18001f8e0;

    private byte* _node;

    /// <summary>
    /// <c>ComputeFrequenciesDbl(double* samples, CTimedLevel* node, int channel)</c> on one explicit window:
    /// the analysis alone, independent of ProcessNbit's buffering. Requires the double path (32-bit input).
    /// Returns the 1024 spectrum bytes it wrote for that channel.
    /// </summary>
    public byte[] Analyze(ReadOnlySpan<short> window, int channel)
    {
        if (_obj[0x1140] == 0) throw new InvalidOperationException("Analyze needs the double path (bitsPerSample > 16).");
        if (_node == null) _node = (byte*)NativeMemory.AllocZeroed(0x1820);
        var samples = (double*)NativeMemory.Alloc(2048 * sizeof(double));
        for (var i = 0; i < 2048; i++) samples[i] = window[i];
        var hr = ((delegate* unmanaged<void*, double*, byte*, int, int>)At(ComputeFrequenciesDblVa))(_obj, samples, _node, channel);
        NativeMemory.Free(samples);
        if (hr < 0) throw new InvalidOperationException($"ComputeFrequenciesDbl failed: 0x{hr:X8}");
        return new ReadOnlySpan<byte>(_node + channel * 0x400, 0x400).ToArray();
    }

    /// <summary>The fast-log tables, read from the loaded module.</summary>
    public static int[] ReadTable(long va, int count)
    {
        var table = new int[count];
        new ReadOnlySpan<int>((void*)At(va), count).CopyTo(table);
        return table;
    }
}
