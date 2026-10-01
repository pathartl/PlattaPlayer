using System.Buffers.Binary;

namespace PlattaPlayer.Wmp.Harness.Synth;

/// <summary>One frame of synthetic audio: the two 1024-byte spectra, the two 1024-byte waveforms, the
/// TimedLevel state and its timestamp. Exactly the payload WMP hands a visualization.</summary>
internal sealed class AudioFrame
{
    public const int Bins = 1024;

    public byte[] Frequency0 { get; } = new byte[Bins];
    public byte[] Frequency1 { get; } = new byte[Bins];
    public byte[] Waveform0 { get; } = new byte[Bins];
    public byte[] Waveform1 { get; } = new byte[Bins];
    public int State { get; set; }
    public long TimeStamp { get; set; }

    public void CopyFrom(AudioFrame other)
    {
        other.Frequency0.CopyTo(Frequency0, 0);
        other.Frequency1.CopyTo(Frequency1, 0);
        other.Waveform0.CopyTo(Waveform0, 0);
        other.Waveform1.CopyTo(Waveform1, 0);
        State = other.State;
        TimeStamp = other.TimeStamp;
    }
}

/// <summary>
/// On-disk container for a synthetic audio sequence. Both the ground-truth capture and our own
/// renderer read the SAME file, so there is no opportunity for the two sides to disagree about their
/// input — any difference in the output frames is a difference in the renderers.
/// </summary>
internal static class AudioFrameSet
{
    private const uint Magic = 0x59535050; // "PPSY"
    private const int Version = 1;
    private const int FrameBytes = AudioFrame.Bins * 4 + 4 + 8;

    public static void Write(string path, IReadOnlyList<AudioFrame> frames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        Span<byte> head = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(head[..4], Magic);
        BinaryPrimitives.WriteInt32LittleEndian(head.Slice(4, 4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(head.Slice(8, 4), frames.Count);
        fs.Write(head);

        Span<byte> tail = stackalloc byte[12];
        foreach (var f in frames)
        {
            fs.Write(f.Frequency0);
            fs.Write(f.Frequency1);
            fs.Write(f.Waveform0);
            fs.Write(f.Waveform1);
            BinaryPrimitives.WriteInt32LittleEndian(tail[..4], f.State);
            BinaryPrimitives.WriteInt64LittleEndian(tail.Slice(4, 8), f.TimeStamp);
            fs.Write(tail);
        }
    }

    public static List<AudioFrame> Read(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[12];
        fs.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head[..4]) != Magic)
            throw new InvalidDataException($"{path} is not a synthetic audio sequence.");
        var version = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(4, 4));
        if (version != Version)
            throw new InvalidDataException($"{path} is version {version}, expected {Version}.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(8, 4));

        var list = new List<AudioFrame>(count);
        Span<byte> tail = stackalloc byte[12];
        for (var i = 0; i < count; i++)
        {
            var f = new AudioFrame();
            fs.ReadExactly(f.Frequency0);
            fs.ReadExactly(f.Frequency1);
            fs.ReadExactly(f.Waveform0);
            fs.ReadExactly(f.Waveform1);
            fs.ReadExactly(tail);
            f.State = BinaryPrimitives.ReadInt32LittleEndian(tail[..4]);
            f.TimeStamp = BinaryPrimitives.ReadInt64LittleEndian(tail.Slice(4, 8));
            list.Add(f);
        }
        return list;
    }

    public static long ExpectedSize(int frameCount) => 12 + (long)frameCount * FrameBytes;
}
