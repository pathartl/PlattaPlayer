using System.Text;

namespace PlattaPlayer.Codecs.Midi;

/// <summary>
/// Reads PlattaPlayer's optional, ID3-style tag block embedded in a Standard MIDI File. MIDI has no
/// standard tagging system, so this is a private convention: a single Text meta event (<c>FF 01</c>)
/// whose payload starts with the marker <c>PLPL\n</c>, followed by one <c>key=value</c> pair per
/// line. Values are URL-encoded (so they may contain '=' or newlines) and read as UTF-8. The marker
/// disambiguates our block from ordinary human-readable text events; files without it are ignored.
/// Also unwraps RMID (RIFF-wrapped) MIDI so <c>.rmi</c> tags are picked up.
/// </summary>
internal static class MidiMetadataReader
{
    private static readonly byte[] MagicBytes = Encoding.UTF8.GetBytes("PLPL\n");

    public static IReadOnlyDictionary<string, string>? TryRead(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            return TryParse(data);
        }
        catch
        {
            // Unreadable or malformed MIDI — treat as "no tags" so one bad file can't abort a reindex.
            return null;
        }
    }

    public static IReadOnlyDictionary<string, string>? TryParse(byte[] data)
    {
        var (smfStart, smfLen) = LocateSmf(data);
        if (smfStart < 0) return null;

        var block = FindTagBlock(data, smfStart, smfStart + smfLen);
        if (block is null) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in block.Split('\n'))
        {
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (key.Length == 0) continue;
            result[key] = Uri.UnescapeDataString(line[(eq + 1)..]);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Locates the embedded Standard MIDI File. Returns the byte offset and length of the
    /// SMF stream, or (-1, 0) if the data is neither a bare SMF nor an RMID container.</summary>
    internal static (int Offset, int Length) LocateSmf(byte[] data)
    {
        if (Matches(data, 0, "MThd"))
            return (0, data.Length);

        // RMID: "RIFF" <le32 size> "RMID" [chunks...]; the SMF lives in the "data" chunk.
        if (data.Length >= 12 && Matches(data, 0, "RIFF") && Matches(data, 8, "RMID"))
        {
            var p = 12;
            while (p + 8 <= data.Length)
            {
                var id = Encoding.ASCII.GetString(data, p, 4);
                var size = data[p + 4] | (data[p + 5] << 8) | (data[p + 6] << 16) | (data[p + 7] << 24);
                p += 8;
                if (size < 0 || p + size > data.Length) size = data.Length - p;
                if (id == "data")
                    return (p, size);
                p += size + (size & 1); // RIFF chunks are word-aligned
            }
        }

        return (-1, 0);
    }

    private static string? FindTagBlock(byte[] data, int start, int limit)
    {
        var pos = start;
        if (pos + 14 > limit || ReadChunkId(data, ref pos) != "MThd")
            return null;

        var headerLen = (int)ReadUInt32(data, ref pos);
        if (headerLen < 0) return null; // corrupt length field — bail rather than seek to a negative offset
        pos += headerLen; // skip the header body (we only need the chunk layout)

        while (pos >= 0 && pos + 8 <= limit)
        {
            var id = ReadChunkId(data, ref pos);
            var len = (int)ReadUInt32(data, ref pos);
            if (len < 0) break; // corrupt chunk length — stop walking
            var trackEnd = (int)Math.Min((long)pos + len, limit);
            if (id == "MTrk")
            {
                var block = ScanTrack(data, pos, trackEnd);
                if (block is not null)
                    return block;
            }
            pos = trackEnd;
        }

        return null;
    }

    /// <summary>Walks one track's events looking for our marked Text meta event, skipping over every
    /// other event type so variable-length data never desynchronizes the reader.</summary>
    private static string? ScanTrack(byte[] data, int start, int end)
    {
        var pos = start;
        byte runningStatus = 0;

        while (pos < end)
        {
            ReadVarLen(data, ref pos, end); // delta-time (unused)
            if (pos >= end) break;

            var b = data[pos];

            if (b == 0xFF) // meta event
            {
                pos++;
                if (pos >= end) break;
                var metaType = data[pos++];
                var len = (int)ReadVarLen(data, ref pos, end);
                if (len < 0 || pos + len > end) break;

                if (metaType == 0x01 && StartsWithMagic(data, pos, len))
                    return Encoding.UTF8.GetString(data, pos + MagicBytes.Length, len - MagicBytes.Length);

                pos += len;
                runningStatus = 0;
            }
            else if (b is 0xF0 or 0xF7) // SysEx — skip
            {
                pos++;
                var len = (int)ReadVarLen(data, ref pos, end);
                pos += len;
                runningStatus = 0;
            }
            else // channel voice message (possibly running status)
            {
                byte status;
                if ((b & 0x80) != 0) { status = b; pos++; runningStatus = status; }
                else { status = runningStatus; if (status == 0) { pos++; continue; } }

                var type = status & 0xF0;
                pos += type is 0xC0 or 0xD0 ? 1 : 2;
            }
        }

        return null;
    }

    private static bool StartsWithMagic(byte[] data, int pos, int available)
    {
        if (available < MagicBytes.Length) return false;
        for (var i = 0; i < MagicBytes.Length; i++)
            if (data[pos + i] != MagicBytes[i])
                return false;
        return true;
    }

    private static bool Matches(byte[] data, int pos, string id)
    {
        if (pos + id.Length > data.Length) return false;
        for (var i = 0; i < id.Length; i++)
            if (data[pos + i] != id[i])
                return false;
        return true;
    }

    private static long ReadVarLen(byte[] data, ref int pos, int end)
    {
        long value = 0;
        while (pos < end)
        {
            var b = data[pos++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    private static string ReadChunkId(byte[] data, ref int pos)
    {
        var id = Encoding.ASCII.GetString(data, pos, 4);
        pos += 4;
        return id;
    }

    private static uint ReadUInt32(byte[] data, ref int pos)
    {
        var v = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;
        return v;
    }
}
