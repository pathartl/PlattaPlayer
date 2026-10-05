using System.Buffers.Binary;
using PlattaPlayer.Codecs.Psf;

namespace PlattaPlayer.Codecs.Gsf;

/// <summary>
/// The memory image a GSF song boots from. Each file's program (after zlib) is a 12-byte header (entry point,
/// load address and data size, little-endian 32-bit) and the data, written at its load address into one image,
/// in psflib order (<see cref="PsfSet"/>): the .gsflib with the game's code first, then the miniGSF's few
/// patched bytes (usually the song number) over it. The entry point is the first file's.
/// <para>The load address's top byte says where the image goes: 0x08 / 0x09 is the cartridge ROM, 0x02 the
/// EWRAM a multiboot program runs from. As in the GSF players, the address is taken modulo 32 MiB, so
/// 0x09000000 lands 16 MiB into the ROM.</para>
/// </summary>
internal sealed class GsfImage
{
    private const int HeaderSize = 12;
    private const int MaxRom = 32 << 20;
    private const int MaxMultiboot = 256 << 10;

    private GsfImage(uint entryPoint, byte[] data)
    {
        EntryPoint = entryPoint;
        Data = data;
    }

    public uint EntryPoint { get; }

    public byte[] Data { get; }

    /// <summary>The image runs from EWRAM rather than from a cartridge.</summary>
    public bool Multiboot => EntryPoint >> 24 == 2;

    /// <summary>Loads <paramref name="path"/> and its libraries. Throws when a file is missing or invalid.</summary>
    public static GsfImage Load(string path) => Build(PsfSet.Load(path, PsfFile.GsfVersion, "GSF"));

    public static GsfImage Build(IReadOnlyList<PsfFile> files)
    {
        uint? entry = null;
        var data = Array.Empty<byte>();
        foreach (var file in files)
        {
            var program = file.Program;
            if (program.IsEmpty) continue; // a file that only adds tags
            if (program.Length < HeaderSize) throw new InvalidDataException("The GSF data is truncated.");

            entry ??= BinaryPrimitives.ReadUInt32LittleEndian(program);
            var offset = (int)(BinaryPrimitives.ReadUInt32LittleEndian(program[4..]) & 0x01FFFFFF);
            // The size field names the data's length; trust it only as far as the data goes.
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(program[8..]), (uint)(program.Length - HeaderSize));

            var end = (long)offset + size;
            if (end > (entry >> 24 == 2 ? MaxMultiboot : MaxRom)) throw new InvalidDataException("The GSF data lies outside the GBA's memory.");
            if (end > data.Length) Array.Resize(ref data, (int)end);
            program.Slice(HeaderSize, size).CopyTo(data.AsSpan(offset));
        }

        if (entry is not { } start || data.Length == 0) throw new InvalidDataException("The GSF has no data.");
        return new GsfImage(start, data);
    }
}
