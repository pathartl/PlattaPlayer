using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>
/// pspDecryptPRX: decrypts a "~PSP" container (encrypted module, PSAR block or file table). Ported from
/// pspdecrypt's PrxDecrypter.cpp (itself from PPSSPP; keys from PSARDUMPER, JPCSP and artart78's uOFW
/// work; GPLv3). Like the original it does not know the container type up front and tries each layout
/// in turn; a layout is accepted when its tag is known and the SHA-1 over the descrambled header
/// matches, and the KIRK1 CMAC checks then pass.
/// <para>
/// Only two of the original five layouts are kept, the ones a 6.xx update PSAR needs (determined by
/// logging the tags hit while extracting from 6.61): type 0 (a 0x90-byte pre-expanded XOR key; the
/// PSAR's own blocks, tag 0x0E000000) and type 2 (a 16-byte seed expanded with KIRK CMD7; the file
/// tables and visualizer_plugin.prx). Types 1, 5 and 6 (older PSARs, pauth/ECDSA-signed modules) are
/// not ported. The tag tables are trimmed to match (see <see cref="PrxKeys"/>).
/// </para>
/// </summary>
internal sealed class PrxDecrypter
{
    /// <summary>sizeof(PSP_Header)</summary>
    private const int PspHeaderSize = 0x150;

    /// <summary>offset = sizeof(PSP_Header) - sizeof(KIRK_CMD1_HEADER) - sizeof(prxHeader)</summary>
    private const int Cmd1HeaderOffset = PspHeaderSize - 0x90 - 0x80;

    private readonly Kirk _kirk;

    public PrxDecrypter(Kirk kirk) => _kirk = kirk;

    /// <summary>The tag (u32 at 0xD0) of the last container passed to <see cref="Decrypt"/>, for diagnostics.</summary>
    public uint LastTag { get; private set; }

    /// <summary>
    /// Decrypts <paramref name="input"/> (the whole container, header included). Returns the plaintext
    /// (length = the size field at 0xB0), or null when no layout/tag matches or a check fails.
    /// </summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> input)
    {
        if (input.Length < PspHeaderSize) return null;
        LastTag = BinaryPrimitives.ReadUInt32LittleEndian(input[0xD0..]);
        return DecryptType0(input) ?? DecryptType2(input);
    }

    /// <summary>pspDecryptType0</summary>
    private byte[]? DecryptType0(ReadOnlySpan<byte> input)
    {
        var tag = PrxKeys.FindTag1(LastTag);
        if (tag is null) return null;
        var xorbuf = tag.Key; // already the expanded 0x90-byte seed: no KIRK7 needed

        // struct PRXType0: tag[4] @0, sha1[0x14] @4, unused[0x28] @0x18, kirkBlock[0x90] @0x40,
        // prxHeader[0x80] @0xD0.
        Span<byte> s = stackalloc byte[PspHeaderSize];
        input.Slice(0xD0, 4).CopyTo(s);
        input.Slice(0xD4, 0x14).CopyTo(s[4..]);
        input.Slice(0xE8, 0x28).CopyTo(s[0x18..]);
        input.Slice(0x110, 0x40).CopyTo(s[0x40..]);
        input.Slice(0x80, 0x50).CopyTo(s[0x80..]);
        input[..0x80].CopyTo(s[0xD0..]);

        Span<byte> sha = stackalloc byte[20];
        using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            h.AppendData(xorbuf.AsSpan(0, 0x14));
            h.AppendData(s.Slice(0x18, 0x28));
            h.AppendData(s.Slice(0x40, 0x90));
            h.AppendData(s.Slice(0xD0, 0x80));
            h.GetHashAndReset(sha);
        }
        if (!sha.SequenceEqual(s.Slice(4, 0x14))) return null;

        // Rebuild a KIRK1 block in place of the PSP header: header @0x40, prxHeader @0xD0, data @0x150.
        var block = input[Cmd1HeaderOffset..].ToArray();
        s.Slice(0x40, 0x90).CopyTo(block);
        s.Slice(0xD0, 0x80).CopyTo(block.AsSpan(0x90));
        // decryptKirkHeaderType0
        for (int i = 0; i < 0x70; i++) block[i] = (byte)(s[0x40 + i] ^ xorbuf[i + 0x14]);
        _kirk.Decrypt7(block.AsSpan(0, 0x70), tag.Code);
        for (int i = 0; i < 0x70; i++) block[i] ^= xorbuf[i + 0x20];

        return RunCmd1(block, input);
    }

    /// <summary>pspDecryptType2</summary>
    private byte[]? DecryptType2(ReadOnlySpan<byte> input)
    {
        var tag = PrxKeys.FindTag2(LastTag);
        if (tag is null) return null;
        // (The original's "0xD4..0x12C must be zero" check is disabled in pspdecrypt: PSAR copies have it set.)

        var xorbuf = ExpandSeed(tag.Key, tag.Code);

        // struct PRXType2: tag[4] @0, empty[0x58] @4, id[0x10] @0x5C, sha1[0x14] @0x6C,
        // kirkHeader[0x40] @0x80, kirkMetadata[0x10] @0xC0, prxHeader[0x80] @0xD0.
        Span<byte> s = stackalloc byte[PspHeaderSize];
        s.Clear();
        input.Slice(0xD0, 4).CopyTo(s);
        input.Slice(0x140, 0x10).CopyTo(s[0x5C..]);
        input.Slice(0x12C, 0x14).CopyTo(s[0x6C..]);
        input.Slice(0x80, 0x30).CopyTo(s[0x80..]); // kirk header is split between 0x80..0xB0 and 0xC0..0xD0
        input.Slice(0xC0, 0x10).CopyTo(s[0xB0..]);
        input.Slice(0xB0, 0x10).CopyTo(s[0xC0..]);
        input[..0x80].CopyTo(s[0xD0..]);
        _kirk.Decrypt7(s.Slice(0x5C, 0x60), tag.Code);

        Span<byte> sha = stackalloc byte[20];
        using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            h.AppendData(s[..4]);
            h.AppendData(xorbuf.AsSpan(0, 0x10));
            h.AppendData(s.Slice(4, 0x58));
            h.AppendData(s.Slice(0x5C, 0x10));
            h.AppendData(s.Slice(0x80, 0x40));
            h.AppendData(s.Slice(0xC0, 0x10));
            h.AppendData(s.Slice(0xD0, 0x80));
            h.GetHashAndReset(sha);
        }
        if (!sha.SequenceEqual(s.Slice(0x6C, 0x14))) return null;

        var block = input[Cmd1HeaderOffset..].ToArray();
        block.AsSpan(0, 0x90).Clear();
        s.Slice(0xC0, 0x10).CopyTo(block.AsSpan(0x70)); // data_size, data_offset, unk
        s.Slice(0xD0, 0x80).CopyTo(block.AsSpan(0x90));
        // decryptKirkHeader
        for (int i = 0; i < 0x40; i++) block[i] = (byte)(s[0x80 + i] ^ xorbuf[0x10 + i]);
        _kirk.Decrypt7(block.AsSpan(0, 0x40), tag.Code);
        for (int i = 0; i < 0x40; i++) block[i] ^= xorbuf[0x50 + i];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x60), 1); // mode = KIRK_MODE_CMD1

        return RunCmd1(block, input);
    }

    /// <summary>expandSeed: nine copies of the seed with the block index in byte 0, CMD7-decrypted (CBC).</summary>
    private byte[] ExpandSeed(byte[] seed, int code)
    {
        var expanded = new byte[0x90];
        for (int i = 0; i < 0x90; i += 0x10)
        {
            seed.AsSpan(0, 0x10).CopyTo(expanded.AsSpan(i));
            expanded[i] = (byte)(i / 0x10);
        }
        _kirk.Decrypt7(expanded, code);
        return expanded;
    }

    private byte[]? RunCmd1(byte[] block, ReadOnlySpan<byte> input)
    {
        int decryptSize = BinaryPrimitives.ReadInt32LittleEndian(input[0xB0..]);
        var plain = _kirk.DecryptCmd1(block);
        if (plain is null || decryptSize < 0 || decryptSize > plain.Length) return null;
        return plain.Length == decryptSize ? plain : plain.AsSpan(0, decryptSize).ToArray();
    }
}
