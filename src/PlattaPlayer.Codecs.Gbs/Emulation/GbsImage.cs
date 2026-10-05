using PlattaPlayer.Codecs.Gbs;

namespace PlattaPlayer.Codecs.Gbs.Emulation;

/// <summary>
/// A GBS's music driver laid out as the cartridge ROM SameBoy builds for it (GB_load_gbs_from_buffer): the
/// data at its load address in a ROM padded with $FF to a power-of-two size, interrupt vectors that jump into
/// the driver's own vector table, and a small player routine at $61 that calls init, then calls play after
/// every interrupt.
/// </summary>
internal sealed class GbsImage
{
    public const int HeaderSize = 0x70;

    /// <summary>Where SameBoy puts its player routine.</summary>
    public const ushort EntryAddress = 0x61;
    public const int EntrySize = 13;

    /// <summary>The most SameBoy's player reads from a file: the header and 256 banks (MBC3's maximum).</summary>
    public const int MaxFileSize = HeaderSize + 0x4000 * 0x100;

    private GbsImage(byte[] rom, GbsHeader header)
    {
        Rom = rom;
        LoadAddress = header.LoadAddress;
        InitAddress = header.InitAddress;
        PlayAddress = header.PlayAddress;
        StackPointer = header.StackPointer;
        Tma = header.Tma;
        Tac = header.Tac;
    }

    public byte[] Rom { get; }
    public ushort LoadAddress { get; }
    public ushort InitAddress { get; }
    public ushort PlayAddress { get; }
    public ushort StackPointer { get; }
    public byte Tma { get; }
    public byte Tac { get; }

    /// <summary>Builds the ROM image; null when SameBoy would reject the file.</summary>
    public static GbsImage? TryCreate(ReadOnlySpan<byte> file)
    {
        if (GbsHeader.TryParse(file) is not { } header) return null;
        if (file.Length > MaxFileSize) file = file[..MaxFileSize];

        var data = file[HeaderSize..];
        var rom = new byte[RoundedRomSize(data.Length + header.LoadAddress)];
        rom.AsSpan().Fill(0xFF);
        data.CopyTo(rom.AsSpan(header.LoadAddress));

        var image = new GbsImage(rom, header);
        if (header.LoadAddress != 0)
        {
            // With TAC bit 6 set the driver handles the timer and serial interrupts too.
            var hasInterrupts = (header.Tac & 0x40) != 0;
            for (var i = 0; i <= (hasInterrupts ? 0x50 : 0x38); i += 8)
            {
                rom[i] = 0xC3;  // jp $XXXX
                rom[i + 1] = (byte)(header.LoadAddress + i);
                rom[i + 2] = (byte)((header.LoadAddress + i) >> 8);
            }
            for (var i = hasInterrupts ? 0x58 : 0x40; i <= 0x60; i += 8) rom[i] = 0xC9;  // ret
            image.Entry().CopyTo(rom, EntryAddress);
        }
        return image;
    }

    /// <summary>The player routine: call init, then forever halt, clear IF and call play.</summary>
    public byte[] Entry() =>
    [
        0xCD, (byte)InitAddress, (byte)(InitAddress >> 8),  // call init
        0x76,                                               // halt
        0x00,                                               // nop
        0xAF,                                               // xor a
        0xE0, 0x0F,                                         // ldh [IF], a
        0xCD, (byte)PlayAddress, (byte)(PlayAddress >> 8),  // call play
        0x18, unchecked((byte)-10),                         // jr halt
    ];

    // A whole number of 16 KiB banks, rounded up to a power of two, at least 32 KiB.
    private static int RoundedRomSize(int size)
    {
        size = (size + 0x3FFF) & ~0x3FFF;
        while ((size & (size - 1)) != 0)
        {
            size |= size >> 1;
            size++;
        }
        return Math.Max(size, 0x8000);
    }
}
