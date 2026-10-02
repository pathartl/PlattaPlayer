using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>
/// The two KIRK (PSP crypto engine) services the PSAR and PRX decryption needs, ported from libkirk's
/// kirk_engine.c (Draan, with coyotebean, Davee, hitchhikr, kgsws, liquidzigong, Mathieulh, Proxima,
/// SilverSpring; GPLv3) as used by pspdecrypt:
/// <list type="bullet">
/// <item>CMD1 "decrypt private": a 0x90-byte header whose first 32 bytes (AES key + CMAC key) are
/// AES-128-CBC encrypted with the fixed KIRK1 key; the body is AES-128-CBC with the recovered key. The
/// CMAC header/data hashes are verified (libkirk's kirk_CMD10) unless the header says ECDSA, whose
/// verification pspdecrypt disables (g_checkEcdsa = 0) and so do we.</item>
/// <item>CMD7 "decrypt, IV 0": AES-128-CBC with a key from the key vault selected by a seed code.</item>
/// </list>
/// AES-CBC always runs with a zero IV, as libkirk does. Instances are not thread-safe (they cache
/// <see cref="Aes"/> objects); use one per extraction.
/// </summary>
internal sealed class Kirk : IDisposable
{
    /// <summary>KIRK1 master key (libkirk kirk1_key).</summary>
    private static readonly byte[] Kirk1Key =
        [0x98, 0xC9, 0x40, 0x97, 0x5C, 0x1D, 0x10, 0xE8, 0x7F, 0xE6, 0x0E, 0xA3, 0xFD, 0x03, 0xA8, 0xBA];

    private static readonly byte[] ZeroIv = new byte[16];

    private readonly Dictionary<int, Aes> _vaultAes = new();
    private readonly Aes _kirk1Aes;
    private readonly Aes _scratch = Aes.Create();

    public Kirk()
    {
        _kirk1Aes = Aes.Create();
        _kirk1Aes.Key = Kirk1Key;
    }

    public void Dispose()
    {
        _kirk1Aes.Dispose();
        _scratch.Dispose();
        foreach (var aes in _vaultAes.Values) aes.Dispose();
        _vaultAes.Clear();
    }

    /// <summary>
    /// kirk7(): AES-128-CBC decrypt (IV 0) of <paramref name="data"/> in place with key vault entry
    /// <paramref name="keyCode"/>. Like libkirk, a trailing partial block is processed as a whole block, so
    /// the span length should be a multiple of 16.
    /// </summary>
    public void Decrypt7(Span<byte> data, int keyCode)
    {
        if (data.Length % 16 != 0) throw new ArgumentException("KIRK CMD7 data must be a multiple of 16 bytes.");
        GetVaultAes(keyCode).DecryptCbc(data, ZeroIv, data, PaddingMode.None);
    }

    /// <summary>
    /// kirk_CMD1: decrypts the KIRK1 block whose 0x90-byte header starts at <paramref name="block"/>[0].
    /// Returns the decrypted body (data_size rounded up to 16 bytes, as libkirk writes it) or null when
    /// the header is malformed, the block is truncated or a CMAC check fails.
    /// </summary>
    public byte[]? DecryptCmd1(ReadOnlySpan<byte> block)
    {
        if (block.Length < 0x90) return null;
        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(block[0x60..]);
        if (mode != 1) return null;
        bool ecdsa = block[0x64] == 1;
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(block[0x70..]);
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(block[0x74..]);
        if (dataSize <= 0 || dataOffset < 0) return null;
        int alignedSize = (dataSize + 15) & ~15;
        long end = 0x90L + dataOffset + alignedSize;
        if (end > block.Length) return null;

        // AES key (block 0) + CMAC key (block 1), CBC with IV 0.
        Span<byte> keys = stackalloc byte[32];
        _kirk1Aes.DecryptCbc(block[..32], ZeroIv, keys, PaddingMode.None);

        if (!ecdsa)
        {
            // kirk_CMD10: CMAC over header[0x60..0x90] and over header[0x60..] + data.
            _scratch.Key = keys[16..32].ToArray();
            Span<byte> mac = stackalloc byte[16];
            AesCmac(_scratch, block.Slice(0x60, 0x30), mac);
            if (!mac.SequenceEqual(block.Slice(0x20, 16))) return null;
            AesCmac(_scratch, block.Slice(0x60, 0x30 + alignedSize + dataOffset), mac);
            if (!mac.SequenceEqual(block.Slice(0x30, 16))) return null;
        }

        _scratch.Key = keys[..16].ToArray();
        var output = new byte[alignedSize];
        _scratch.DecryptCbc(block.Slice(0x90 + dataOffset, alignedSize), ZeroIv, output, PaddingMode.None);
        CryptographicOperations.ZeroMemory(keys);
        return output;
    }

    private Aes GetVaultAes(int keyCode)
    {
        if (!_vaultAes.TryGetValue(keyCode, out var aes))
        {
            if (!PrxKeys.TryGetVaultKey(keyCode, out var key))
                throw new PspFirmwareException($"KIRK key vault entry 0x{keyCode:X2} is not included in this build.");
            aes = Aes.Create();
            aes.Key = key;
            _vaultAes[keyCode] = aes;
        }
        return aes;
    }

    /// <summary>AES-CMAC (RFC 4493), as libkirk's AES_CMAC.</summary>
    private static void AesCmac(Aes aes, ReadOnlySpan<byte> message, Span<byte> mac)
    {
        Span<byte> l = stackalloc byte[16];
        aes.EncryptEcb(ZeroIv, l, PaddingMode.None);
        Span<byte> k1 = stackalloc byte[16];
        Span<byte> k2 = stackalloc byte[16];
        ShiftLeftXor(l, k1);
        ShiftLeftXor(k1, k2);

        int n = (message.Length + 15) / 16;
        bool complete;
        if (n == 0)
        {
            n = 1;
            complete = false;
        }
        else
        {
            complete = message.Length % 16 == 0;
        }

        Span<byte> last = stackalloc byte[16];
        int lastStart = (n - 1) * 16;
        if (complete)
        {
            for (int i = 0; i < 16; i++) last[i] = (byte)(message[lastStart + i] ^ k1[i]);
        }
        else
        {
            int rem = message.Length - lastStart;
            last.Clear();
            message[lastStart..].CopyTo(last);
            last[rem] = 0x80;
            for (int i = 0; i < 16; i++) last[i] ^= k2[i];
        }

        // CBC-MAC: encrypt all full blocks before the last with CBC (IV 0), keep the chaining value.
        Span<byte> x = stackalloc byte[16];
        x.Clear();
        if (lastStart > 0)
        {
            var tmp = lastStart <= 4096 ? stackalloc byte[lastStart] : new byte[lastStart];
            aes.EncryptCbc(message[..lastStart], ZeroIv, tmp, PaddingMode.None);
            tmp[(lastStart - 16)..].CopyTo(x);
        }
        for (int i = 0; i < 16; i++) x[i] ^= last[i];
        aes.EncryptEcb(x, mac, PaddingMode.None);
    }

    private static void ShiftLeftXor(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int carry = 0;
        for (int i = 15; i >= 0; i--)
        {
            int b = input[i];
            output[i] = (byte)((b << 1) | carry);
            carry = b >> 7;
        }
        if ((input[0] & 0x80) != 0) output[15] ^= 0x87;
    }
}
