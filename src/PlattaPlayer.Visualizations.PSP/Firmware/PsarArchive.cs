using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>
/// Reads files out of the DATA.PSAR section of an official PSP firmware update EBOOT.PBP. A port of
/// pspPSARInit / pspPSARGetNextFile / pspDecryptPSAR from pspdecrypt's PsarDecrypter.cpp (from
/// newpsardumper-660: PspPet, with Vampire, Nem, Dark_AleX, Noobz, Team C+D, M33 Team, bbtgp, Proxima,
/// some1; PC adaptation by artart78; GPLv3) and pspDecryptTable from pspdecrypt_lib.cpp.
/// <para>
/// A PSAR is a sequence of entries, each a 0x110-byte descriptor (name, data chunk size, expanded size)
/// followed by a data chunk; both are encrypted "~PSP" containers whose plaintext header area is
/// additionally mangled with KIRK CMD7 (<see cref="Demangle"/>). Data chunks are zlib streams. Most file
/// names are 5-digit numbers resolved through per-model file tables, which are themselves PSAR entries
/// (DES-CBC + PRX encrypted) near the start of the archive.
/// </para>
/// <para>
/// Unlike pspdecrypt, which decrypts and inflates every entry, this walker decrypts only each entry's
/// descriptor and skips the data chunk unless it is a file table or one of the requested files, and
/// stops as soon as all requested files are found.
/// </para>
/// </summary>
internal sealed class PsarArchive : IDisposable
{
    private const int Overhead = 0x150;
    private const int SizeA = 0x110; // size of a decoded file descriptor

    private const uint PbpMagic = 0x50425000; // "\0PBP"
    private const uint PsarMagic = 0x52415350; // "PSAR"

    /// <summary>g_tableFilenames: entry-name prefix -> table slot (0 = common, else model number).</summary>
    private static readonly (string Prefix, int Slot)[] TableFilenames =
    [
        ("com:00000", 0), ("01g:00000", 1), ("02g:00000", 2),
        ("00001", 1), ("00002", 2), ("00003", 3), ("00004", 4), ("00005", 5), ("00006", 6),
        ("00007", 7), ("00008", 8), ("00009", 9), ("00011", 11), ("00012", 12),
    ];

    /// <summary>table_keys: DES key + IV per table_mode.</summary>
    private static readonly (byte[] Key, byte[] Iv)[] TableKeys =
    [
        ([0x95, 0x62, 0x0B, 0x49, 0xB7, 0x30, 0xE5, 0xC7], [0x9E, 0xA4, 0x33, 0x81, 0x86, 0x0C, 0x52, 0x85]),
        ([0x5A, 0x7B, 0x3D, 0x9D, 0x45, 0xC9, 0xDC, 0x95], [0xB2, 0xFE, 0xD9, 0x79, 0x8A, 0x02, 0xB1, 0x87]),
        ([0x4C, 0xCE, 0x49, 0x5B, 0x6F, 0x20, 0x58, 0x5A], [0x81, 0x08, 0xC1, 0xF2, 0x35, 0x98, 0x69, 0xB0]),
        ([0x73, 0xF4, 0x52, 0x62, 0x62, 0x0B, 0xF1, 0x5A], [0x6D, 0x52, 0x1B, 0xA3, 0xC2, 0x36, 0xF9, 0x2B]),
        ([0xA6, 0x64, 0xC8, 0xF8, 0xFD, 0x9D, 0x44, 0x98], [0xDB, 0x4E, 0x79, 0x41, 0xF5, 0x97, 0x30, 0xAD]),
        ([0xD7, 0xBD, 0x74, 0x81, 0x3D, 0x64, 0x26, 0xE7], [0xA6, 0x83, 0x0C, 0x2F, 0x63, 0x0B, 0x96, 0x29]),
    ];

    /// <summary>Demangle XOR keys for PSAR version 5.</summary>
    private static readonly byte[] DemangleK1 =
        [0xD8, 0x69, 0xB8, 0x95, 0x33, 0x6B, 0x63, 0x34, 0x98, 0xB9, 0xFC, 0x3C, 0xB7, 0x26, 0x2B, 0xD7];

    private static readonly byte[] DemangleK2 =
        [0x0D, 0xA0, 0x90, 0x84, 0xAF, 0x9E, 0xB6, 0xE2, 0xD2, 0x94, 0xF2, 0xAA, 0xEF, 0x99, 0x68, 0x71];

    private readonly SafeFileHandle _file;
    private readonly long _psarOffset;
    private readonly long _psarLength;
    private readonly Kirk _kirk = new();
    private readonly PrxDecrypter _prx;
    private readonly byte[]?[] _tables = new byte[13][];
    private int _psarVersion;
    private int _tableMode;
    private int _intVersion;
    private long _iBase;

    private PsarArchive(SafeFileHandle file, long psarOffset, long psarLength)
    {
        _file = file;
        _psarOffset = psarOffset;
        _psarLength = psarLength;
        _prx = new PrxDecrypter(_kirk);
    }

    /// <summary>Firmware version string from the PSAR's first record, e.g. "6.61".</summary>
    public string FirmwareVersion { get; private set; } = "";

    /// <summary>Tags (u32 at 0xD0) of every container decrypted so far, for diagnostics.</summary>
    public HashSet<uint> TagsSeen { get; } = new();

    /// <summary>
    /// Opens an EBOOT.PBP (or a bare DATA.PSAR) and validates the PSAR's first records. Throws
    /// <see cref="PspFirmwareException"/> when the file is not a usable official firmware update.
    /// </summary>
    public static PsarArchive Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            long length = RandomAccess.GetLength(handle);
            Span<byte> head = stackalloc byte[0x28];
            if (length < head.Length || RandomAccess.Read(handle, head, 0) != head.Length)
                throw new PspFirmwareException("the file is too small to be a PSP firmware update.");

            long psarOffset;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
            if (magic == PbpMagic)
            {
                // PBP header: magic, version, then offsets of PARAM.SFO, ICON0, ICON1, PIC0, PIC1, SND0,
                // DATA.PSP, DATA.PSAR. The PSAR runs to the end of the file.
                psarOffset = BinaryPrimitives.ReadUInt32LittleEndian(head[0x24..]);
                if (psarOffset == 0 || psarOffset >= length - 0x10)
                    throw new PspFirmwareException("the EBOOT.PBP has no DATA.PSAR, so it isn't a PSP firmware update.");
            }
            else if (magic == PsarMagic)
            {
                psarOffset = 0;
            }
            else
            {
                throw new PspFirmwareException("the file isn't a PSP EBOOT.PBP.");
            }

            var archive = new PsarArchive(handle, psarOffset, length - psarOffset);
            archive.Init();
            return archive;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _kirk.Dispose();
        _file.Dispose();
    }

    /// <summary>
    /// Walks the archive and returns the expanded (inflated, still possibly "~PSP"-encrypted) contents
    /// of the requested files, keyed by full path like "flash0:/vsh/module/visualizer_plugin.prx".
    /// Files that are not present are missing from the result.
    /// </summary>
    public Dictionary<string, byte[]> Extract(IReadOnlyCollection<string> wanted, CancellationToken cancel = default)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (result.Count < wanted.Count && _iBase < _psarLength - Overhead)
        {
            cancel.ThrowIfCancellationRequested();

            // pspPSARGetNextFile: the descriptor
            var desc = DecodeBlock(_iBase, Overhead + SizeA);
            if (desc is null || desc.Length != SizeA)
                throw new PspFirmwareException(DecryptFailure("a PSAR file descriptor"));
            string name = ReadCString(desc.AsSpan(4, 0xFC));
            uint pl0 = BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(0x100));
            uint cbDataChunk = BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(0x104)); // incl. overhead
            uint cbExpanded = BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(0x108));
            if (pl0 != 0) throw new PspFirmwareException("the PSAR is corrupt (bad file descriptor).");
            long dataPos = _iBase + Overhead + SizeA;
            _iBase = dataPos + cbDataChunk;

            if (!ResolveName(ref name)) continue; // pspdecrypt: "cannot find path of ..."

            int tableSlot = -1;
            bool isFile = name.StartsWith("flash0:/", StringComparison.Ordinal) ||
                          name.StartsWith("flash1:/", StringComparison.Ordinal);
            if (!isFile)
            {
                foreach (var (prefix, slot) in TableFilenames)
                {
                    if (name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        tableSlot = slot;
                        break;
                    }
                }
            }

            bool want = tableSlot >= 0 || (isFile && wanted.Contains(name) && !result.ContainsKey(name));
            if (!want || cbExpanded == 0) continue; // skip the data chunk without decrypting it
            if (cbExpanded > 64 * 1024 * 1024 || cbDataChunk > int.MaxValue)
                throw new PspFirmwareException("the PSAR is corrupt (implausible entry size).");

            var data = InflateChunk(dataPos, (int)cbDataChunk, (int)cbExpanded, name);
            if (tableSlot >= 0)
                _tables[tableSlot] = DecryptTable(data, name);
            else
                result[name] = data;
        }
        return result;
    }

    /// <summary>pspPSARInit + the version / table_mode logic of pspDecryptPSAR.</summary>
    private void Init()
    {
        Span<byte> head = stackalloc byte[0x24];
        RandomAccess.Read(_file, head, _psarOffset);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != PsarMagic)
            throw new PspFirmwareException("the EBOOT.PBP's DATA.PSAR is invalid, so it isn't a PSP firmware update.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(head[0x20..]) == 0x2C333333)
            throw new PspFirmwareException("this is a pre-decrypted custom firmware PSAR; an official Sony update EBOOT.PBP is needed.");
        _psarVersion = head[4];

        var first = DecodeBlock(0x10, Overhead + SizeA);
        if (first is null) throw new PspFirmwareException(DecryptFailure("the PSAR header"));
        if (first.Length != SizeA) throw new PspFirmwareException("the PSAR header has an unexpected size.");
        _iBase = 0x10 + Overhead + SizeA;

        if (_psarVersion != 1)
        {
            // second block (its length varies by version, hence the retries)
            var second = DecodeBlock(_iBase, Overhead + 100)
                         ?? DecodeBlock(_iBase, Overhead + 144)
                         ?? DecodeBlock(_iBase, Overhead + BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(0x90)));
            if (second is null) throw new PspFirmwareException(DecryptFailure("the PSAR version record"));
            int cbChunk = (second.Length + 15) & ~15;
            _iBase += Overhead + cbChunk;
        }

        // GetVersion: the text after the last ',' of the string at 0x10, e.g. "...,6.61".
        string info = ReadCString(first.AsSpan(0x10));
        int comma = info.LastIndexOf(',');
        string version = comma < 0 ? "1.00" : info[(comma + 1)..];
        if (version.Length > 9) version = version[..9];
        if (version.Length != 4 || version[1] != '.' || !char.IsAsciiDigit(version[0]) ||
            !char.IsAsciiDigit(version[2]) || !char.IsAsciiDigit(version[3]))
            throw new PspFirmwareException($"the PSAR reports an invalid firmware version \"{version}\".");
        FirmwareVersion = version;
        _intVersion = (version[0] - '0') * 100 + (version[2] - '0') * 10 + (version[3] - '0');

        _tableMode = _intVersion switch
        {
            >= 380 and < 400 => 1,
            >= 400 and < 500 => 2,
            >= 500 and < 600 => 3,
            >= 610 and < 630 when _psarVersion == 5 => 5,
            >= 600 and < 700 => 4,
            _ => 0,
        };
    }

    /// <summary>The name resolution part of pspDecryptPSAR's main loop. False = unresolvable, skip.</summary>
    private bool ResolveName(ref string name)
    {
        if (Is5DigitNumber(name))
        {
            int n = int.Parse(name, System.Globalization.CultureInfo.InvariantCulture);
            if (n >= 100 || (n >= 10 && _intVersion < 660))
            {
                foreach (var table in _tables)
                {
                    if (table is { Length: > 0 } && FindTablePath(table, name, out var path))
                    {
                        name = path;
                        return true;
                    }
                }
                return false;
            }
            return true;
        }

        int slot = name.StartsWith("com:", StringComparison.Ordinal) ? 0
            : name.StartsWith("01g:", StringComparison.Ordinal) ? 1
            : name.StartsWith("02g:", StringComparison.Ordinal) ? 2
            : -1;
        if (slot >= 0 && _tables[slot] is { Length: > 0 } t)
        {
            if (!FindTablePath(t, name.Length >= 9 ? name.Substring(4, 5) : name[4..], out var path)) return false;
            name = path;
        }
        return true;
    }

    private static bool Is5DigitNumber(string s) => s.Length == 5 && s.All(char.IsAsciiDigit);

    /// <summary>
    /// FindTablePath: finds "<paramref name="number"/>" followed by a separator in the table text and
    /// returns the path after it. Old tables wrote "NNNNN|flash0xxx" / "|iplxxx" with the ':' '/' missing;
    /// those are reinserted exactly as the original does.
    /// </summary>
    private static bool FindTablePath(byte[] table, string number, out string path)
    {
        Span<byte> num = stackalloc byte[5];
        for (int n = 0; n < 5; n++) num[n] = n < number.Length ? (byte)number[n] : (byte)0;
        var sb = new StringBuilder();
        for (int i = 0; i < table.Length - 5; i++)
        {
            if (!table.AsSpan(i, 5).SequenceEqual(num)) continue;
            bool pipe = table[i + 5] == (byte)'|';
            bool flash = pipe && StartsWith(table, i + 6, "flash"u8);
            bool ipl = pipe && StartsWith(table, i + 6, "ipl"u8);
            var chars = new List<char>();
            for (int j = 0; ; j++)
            {
                int at = i + j + 6;
                if (at >= table.Length || (sbyte)table[at] < 0x20) break;
                if (flash && j == 6)
                {
                    SetAt(chars, 6, ':');
                    SetAt(chars, 7, '/');
                }
                else if (ipl && j == 3)
                {
                    SetAt(chars, 3, ':');
                    SetAt(chars, 4, '/');
                }
                else
                {
                    SetAt(chars, chars.Count, (char)table[at]);
                }
            }
            sb.Append(chars.ToArray());
            path = sb.ToString();
            return true;
        }
        path = "";
        return false;

        static void SetAt(List<char> list, int index, char c)
        {
            while (list.Count <= index) list.Add('\0');
            list[index] = c;
        }
    }

    private static bool StartsWith(byte[] data, int offset, ReadOnlySpan<byte> prefix) =>
        offset + prefix.Length <= data.Length && data.AsSpan(offset, prefix.Length).SequenceEqual(prefix);

    /// <summary>pspDecryptPRX on an extracted "~PSP" module, with this archive's KIRK context.</summary>
    public byte[]? DecryptPrx(byte[] module)
    {
        var plain = _prx.Decrypt(module);
        TagsSeen.Add(_prx.LastTag);
        return plain;
    }

    /// <summary>Tag of the last container decrypted (or attempted).</summary>
    public uint LastPrxTag => _prx.LastTag;

    /// <summary>Reads, decrypts and inflates a data chunk (pspPSARGetNextFile's cbExpanded &gt; 0 path).</summary>
    private byte[] InflateChunk(long pos, int cbDataChunk, int cbExpanded, string name)
    {
        var plain = DecodeBlock(pos, cbDataChunk);
        if (plain is null) throw new PspFirmwareException(DecryptFailure($"\"{name}\""));
        if (plain.Length <= 10 || plain[0] != 0x78 || plain[1] != 0x9C)
            throw new PspFirmwareException($"the PSAR entry \"{name}\" isn't zlib data as expected.");

        var output = new byte[cbExpanded];
        try
        {
            using var z = new ZLibStream(new MemoryStream(plain, writable: false), CompressionMode.Decompress);
            int total = 0;
            while (total < cbExpanded)
            {
                int n = z.Read(output, total, cbExpanded - total);
                if (n == 0) break;
                total += n;
            }
            if (total != cbExpanded)
                throw new PspFirmwareException($"the PSAR entry \"{name}\" inflated to {total} bytes, expected {cbExpanded}.");
        }
        catch (InvalidDataException e)
        {
            throw new PspFirmwareException($"the PSAR entry \"{name}\" has corrupt zlib data.", e);
        }
        return output;
    }

    /// <summary>pspDecryptTable: DES-CBC with the table_mode key, then (PSAR version != 4) pspDecryptPRX.</summary>
    private byte[] DecryptTable(byte[] data, string name)
    {
        var (key, iv) = TableKeys[_tableMode];
        int len = (data.Length + 7) & ~7; // OpenSSL processes a trailing partial block as a full one
        var buf = new byte[len];
        data.CopyTo(buf, 0);
        using (var des = DES.Create())
        {
            des.Key = key;
            des.DecryptCbc(buf, iv, buf, PaddingMode.None);
        }
        if (len != data.Length) Array.Resize(ref buf, data.Length);
        if (_psarVersion == 4) return buf;

        var plain = _prx.Decrypt(buf);
        TagsSeen.Add(_prx.LastTag);
        return plain ?? throw new PspFirmwareException(DecryptFailure($"the file table \"{name}\""));
    }

    /// <summary>
    /// DecodeBlock: reads an encrypted block of <paramref name="cbIn"/> bytes at PSAR offset
    /// <paramref name="pos"/>, demangles its header (PSAR version != 1) and decrypts it.
    /// </summary>
    private byte[]? DecodeBlock(long pos, int cbIn)
    {
        if (cbIn < Overhead || pos < 0 || pos >= _psarLength) return null;
        // pspdecrypt copies "a little more for $10 page alignment"
        int len = (int)Math.Min(cbIn + 0x10L, _psarLength - pos);
        if (len < Overhead) return null;
        var block = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = RandomAccess.Read(_file, block.AsSpan(read), _psarOffset + pos + read);
            if (n <= 0) return null;
            read += n;
        }

        if (_psarVersion != 1) Demangle(block.AsSpan(0x20, 0x130));
        var plain = _prx.Decrypt(block);
        TagsSeen.Add(_prx.LastTag);
        return plain;
    }

    /// <summary>
    /// Demangle: "for 1.50 and later, they mangled the plaintext parts of the header". The 0x130 bytes at
    /// 0x20 go through KIRK CMD7 with seed 0x55, bracketed by XOR keys on PSAR version 5.
    /// </summary>
    private void Demangle(Span<byte> data)
    {
        if (_psarVersion == 5)
            for (int i = 0; i < 0x130; i++) data[i] ^= DemangleK1[i & 0xF];
        _kirk.Decrypt7(data, 0x55);
        if (_psarVersion == 5)
            for (int i = 0; i < 0x130; i++) data[i] ^= DemangleK2[i & 0xF];
    }

    private string DecryptFailure(string what) =>
        $"couldn't decrypt {what} (tag 0x{_prx.LastTag:X8}); this firmware version may not be supported.";

    private static string ReadCString(ReadOnlySpan<byte> s)
    {
        int end = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? s : s[..end]);
    }
}
