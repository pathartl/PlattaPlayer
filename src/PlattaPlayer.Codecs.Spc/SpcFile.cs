using System.Text;

namespace PlattaPlayer.Codecs.Spc;

/// <summary>
/// An SPC file: a snapshot of the SNES audio unit mid-song. Layout: a 256-byte header (signature, CPU
/// registers, the ID666 tag), 64 KiB of audio RAM at $100, the 128 DSP registers at $10100, the 64 bytes of
/// RAM hidden under the IPL ROM at $101C0, and an optional extended ("xid6") tag from $10200.
/// </summary>
internal sealed class SpcFile
{
    public const int HeaderSize = 0x100;
    public const int RamOffset = 0x100;
    public const int DspOffset = 0x10100;
    public const int ExtraRamOffset = 0x101c0;

    /// <summary>The size of everything up to the extended tag.</summary>
    public const int SnapshotSize = 0x10200;

    // Only the first 27 characters are checked: some dumpers vary the version that follows.
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("SNES-SPC700 Sound File Data");

    private readonly byte[] _data;

    private SpcFile(byte[] data) => _data = data;

    public int Pc => _data[0x25] | _data[0x26] << 8;
    public byte A => _data[0x27];
    public byte X => _data[0x28];
    public byte Y => _data[0x29];
    public byte Psw => _data[0x2a];
    public byte Sp => _data[0x2b];

    public ReadOnlySpan<byte> Ram => _data.AsSpan(RamOffset, 0x10000);
    public ReadOnlySpan<byte> DspRegisters => _data.AsSpan(DspOffset, 128);

    public ReadOnlySpan<byte> ExtraRam => _data.AsSpan(ExtraRamOffset, 64);

    /// <summary>The whole file.</summary>
    public byte[] Data => _data;

    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    /// <summary>Parses a file's bytes; null when they aren't an SPC snapshot.</summary>
    public static SpcFile? TryParse(byte[] data) =>
        data.Length >= SnapshotSize && HasSignature(data) ? new SpcFile(data) : null;

    public static SpcFile? TryLoad(string path)
    {
        try
        {
            return TryParse(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
