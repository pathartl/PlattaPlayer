using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PlattaPlayer.Wmp.Harness.Imaging;

/// <summary>
/// On-disk container for a rendered frame sequence (BGRA, top-down, stride = width*4). Used for both
/// ground-truth captures and our own renders so the diff verb can load either side identically.
/// </summary>
internal sealed class ImageFrameSet
{
    private const uint Magic = 0x52465050; // "PPFR"
    private const int Version = 1;

    public int Width { get; }
    public int Height { get; }
    public List<byte[]> Frames { get; }

    public ImageFrameSet(int width, int height)
    {
        Width = width;
        Height = height;
        Frames = [];
    }

    private ImageFrameSet(int width, int height, List<byte[]> frames)
    {
        Width = width;
        Height = height;
        Frames = frames;
    }

    public int FrameBytes => Width * Height * 4;

    public void Add(byte[] bgra)
    {
        if (bgra.Length != FrameBytes)
            throw new ArgumentException($"Frame is {bgra.Length} bytes, expected {FrameBytes}.");
        Frames.Add(bgra);
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        Span<byte> head = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(head[..4], Magic);
        BinaryPrimitives.WriteInt32LittleEndian(head.Slice(4, 4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(head.Slice(8, 4), Width);
        BinaryPrimitives.WriteInt32LittleEndian(head.Slice(12, 4), Height);
        BinaryPrimitives.WriteInt32LittleEndian(head.Slice(16, 4), Frames.Count);
        fs.Write(head);
        foreach (var f in Frames) fs.Write(f);
    }

    public static ImageFrameSet Read(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[20];
        fs.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head[..4]) != Magic)
            throw new InvalidDataException($"{path} is not a frame set.");
        var version = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(4, 4));
        if (version != Version)
            throw new InvalidDataException($"{path} is version {version}, expected {Version}.");
        var w = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(8, 4));
        var h = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(12, 4));
        var count = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(16, 4));

        var frames = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var buf = new byte[w * h * 4];
            fs.ReadExactly(buf);
            frames.Add(buf);
        }
        return new ImageFrameSet(w, h, frames);
    }

    /// <summary>Content hash of every frame, used to prove a capture run is reproducible.</summary>
    public string ContentHash()
    {
        var sha = SHA256.Create();
        Span<byte> dims = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dims[..4], Width);
        BinaryPrimitives.WriteInt32LittleEndian(dims.Slice(4, 4), Height);
        sha.TransformBlock(dims.ToArray(), 0, 8, null, 0);
        foreach (var f in Frames) sha.TransformBlock(f, 0, f.Length, null, 0);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..16];
    }

    /// <summary>Number of frames that differ from their predecessor — a cheap "is anything moving?" check.</summary>
    public int DistinctConsecutiveFrames()
    {
        var n = 0;
        for (var i = 1; i < Frames.Count; i++)
            if (!Frames[i].AsSpan().SequenceEqual(Frames[i - 1])) n++;
        return n;
    }

    public void WritePngs(string dir, IEnumerable<int> indices)
    {
        Directory.CreateDirectory(dir);
        foreach (var i in indices)
        {
            if (i < 0 || i >= Frames.Count) continue;
            PngWriter.WriteBgra(Path.Combine(dir, $"frame{i:D4}.png"), Frames[i], Width, Height);
        }
    }
}
