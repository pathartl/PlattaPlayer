using System.Text;

namespace PlattaPlayer.Codecs.Midi;

/// <summary>
/// Writes PlattaPlayer's ID3-style tag block into a Standard MIDI File, mirroring
/// <see cref="MidiMetadataReader"/> exactly so written tags round-trip through it. The block is a
/// single Text meta event (<c>FF 01</c>) whose payload starts with the marker <c>PLPL\n</c> followed
/// by URL-encoded <c>key=value</c> lines. The event is placed at the very start (delta 0) of the
/// first track; any pre-existing PLPL block is removed first. Running status in the rewritten track
/// is expanded into explicit status bytes (valid SMF) so deletion can never desynchronize the stream.
/// RMID (RIFF-wrapped) files are re-wrapped with their other chunks preserved.
/// </summary>
internal static class MidiTagWriter
{
    private static readonly byte[] MagicBytes = Encoding.UTF8.GetBytes("PLPL\n");

    public static void Write(string path, MidiTags tags)
    {
        var data = File.ReadAllBytes(path);

        var (smfStart, smfLen) = MidiMetadataReader.LocateSmf(data);
        if (smfStart < 0)
            throw new InvalidDataException($"Not a valid MIDI/RMID file: {path}");

        var smf = data[smfStart..(smfStart + smfLen)];
        var newSmf = RewriteSmf(smf, tags);

        // RMID container: preserve the RIFF wrapper and other chunks, swap only the SMF ("data").
        var isRmid = data.Length >= 12 && Matches(data, 0, "RIFF") && Matches(data, 8, "RMID");
        var output = isRmid ? RewrapRmid(data, newSmf) : newSmf;

        var tmp = path + ".plpltmp";
        File.WriteAllBytes(tmp, output);
        File.Move(tmp, path, overwrite: true);
    }

    private static byte[] RewriteSmf(byte[] smf, MidiTags tags)
    {
        if (!Matches(smf, 0, "MThd"))
            throw new InvalidDataException("Missing MThd header.");

        var headerLen = (int)ReadU32(smf, 4);
        var ntrks = (smf[10] << 8) | smf[11];

        // Collect every chunk (id + content range) so non-MTrk chunks are preserved verbatim.
        var chunks = new List<(string Id, int Start, int Len)>();
        var pos = 8 + headerLen;
        while (pos + 8 <= smf.Length)
        {
            var id = Encoding.ASCII.GetString(smf, pos, 4);
            var len = (int)ReadU32(smf, pos + 4);
            var contentStart = pos + 8;
            if (len < 0 || contentStart + len > smf.Length)
                len = smf.Length - contentStart;
            chunks.Add((id, contentStart, len));
            pos = contentStart + len;
        }

        var tagEvent = BuildTagEvent(tags); // delta-0 FF 01 block (empty array clears tags)

        var output = new List<byte>(smf.Length + tagEvent.Length + 16);
        var hasMtrk = chunks.Exists(c => c.Id == "MTrk");
        var newNtrks = hasMtrk ? ntrks : ntrks + 1;

        // MThd: keep the original header body but patch the (possibly bumped) track count.
        var headerBody = smf[8..(8 + headerLen)];
        if (headerBody.Length >= 4)
        {
            headerBody[2] = (byte)(newNtrks >> 8);
            headerBody[3] = (byte)newNtrks;
        }
        output.AddRange(Encoding.ASCII.GetBytes("MThd"));
        WriteU32(output, (uint)headerLen);
        output.AddRange(headerBody);

        var firstMtrkDone = false;
        foreach (var (id, start, len) in chunks)
        {
            byte[] content;
            if (id == "MTrk" && !firstMtrkDone)
            {
                content = RebuildTrack(smf, start, start + len, tagEvent);
                firstMtrkDone = true;
            }
            else
            {
                content = smf[start..(start + len)];
            }

            output.AddRange(Encoding.ASCII.GetBytes(id));
            WriteU32(output, (uint)content.Length);
            output.AddRange(content);
        }

        // No track to host the tag — append a minimal one (only when we actually have a block).
        if (!hasMtrk && tagEvent.Length > 0)
        {
            var content = new List<byte>(tagEvent);
            content.AddRange(new byte[] { 0x00, 0xFF, 0x2F, 0x00 }); // End of Track
            output.AddRange(Encoding.ASCII.GetBytes("MTrk"));
            WriteU32(output, (uint)content.Count);
            output.AddRange(content);
        }

        return output.ToArray();
    }

    /// <summary>Rewrites one track's event stream: drops any existing PLPL block, prepends the new
    /// one, and expands running status into explicit status bytes.</summary>
    private static byte[] RebuildTrack(byte[] data, int start, int end, byte[] tagEvent)
    {
        var outp = new List<byte>((end - start) + tagEvent.Length + 8);
        outp.AddRange(tagEvent); // new block at delta 0 (empty when clearing)

        var pos = start;
        byte runningStatus = 0;
        long carriedDelta = 0; // delta of a dropped event, carried onto the next one

        while (pos < end)
        {
            var delta = ReadVarLen(data, ref pos, end) + carriedDelta;
            carriedDelta = 0;
            if (pos >= end) break;

            var b = data[pos];

            if (b == 0xFF) // meta event
            {
                pos++;
                if (pos >= end) break;
                var metaType = data[pos++];
                var len = (int)ReadVarLen(data, ref pos, end);
                if (len < 0 || pos + len > end) break;
                var payloadStart = pos;
                pos += len;

                if (metaType == 0x01 && StartsWithMagic(data, payloadStart, len))
                {
                    carriedDelta = delta; // drop our old block, preserve timing
                    runningStatus = 0;
                    continue;
                }

                WriteVarLen(outp, delta);
                outp.Add(0xFF);
                outp.Add(metaType);
                WriteVarLen(outp, len);
                outp.AddRange(data[payloadStart..(payloadStart + len)]);
                runningStatus = 0;
            }
            else if (b is 0xF0 or 0xF7) // SysEx
            {
                pos++;
                var len = (int)ReadVarLen(data, ref pos, end);
                if (len < 0 || pos + len > end) break;
                var payloadStart = pos;
                pos += len;

                WriteVarLen(outp, delta);
                outp.Add(b);
                WriteVarLen(outp, len);
                outp.AddRange(data[payloadStart..(payloadStart + len)]);
                runningStatus = 0;
            }
            else // channel voice message (possibly running status)
            {
                byte status;
                if ((b & 0x80) != 0) { status = b; pos++; runningStatus = status; }
                else { status = runningStatus; if (status == 0) { pos++; continue; } }

                var dataBytes = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                WriteVarLen(outp, delta);
                outp.Add(status); // explicit status (expands any running status)
                for (var i = 0; i < dataBytes && pos < end; i++)
                    outp.Add(data[pos++]);
            }
        }

        return outp.ToArray();
    }

    /// <summary>Builds the delta-0 <c>FF 01</c> tag event, or an empty array when no fields are set
    /// (so saving an empty form simply strips any existing block).</summary>
    private static byte[] BuildTagEvent(MidiTags tags)
    {
        var sb = new StringBuilder();

        void AddText(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                sb.Append(key).Append('=').Append(Uri.EscapeDataString(value.Trim())).Append('\n');
        }

        void AddInt(string key, int? value)
        {
            if (value is int n && n > 0)
                sb.Append(key).Append('=').Append(n).Append('\n');
        }

        AddText("title", tags.Title);
        AddText("artist", tags.Artist);
        AddText("albumartist", tags.AlbumArtist);
        AddText("album", tags.Album);
        AddText("genre", tags.Genre);
        AddInt("year", tags.Year);
        AddInt("track", tags.Track);
        AddInt("disc", tags.Disc);
        AddText("cover", tags.Cover);
        AddText("soundfont", tags.SoundFont);

        if (sb.Length == 0)
            return Array.Empty<byte>();

        var payload = new byte[MagicBytes.Length + Encoding.UTF8.GetByteCount(sb.ToString())];
        Array.Copy(MagicBytes, payload, MagicBytes.Length);
        Encoding.UTF8.GetBytes(sb.ToString(), 0, sb.Length, payload, MagicBytes.Length);

        var ev = new List<byte> { 0x00, 0xFF, 0x01 }; // delta 0, meta, text
        WriteVarLen(ev, payload.Length);
        ev.AddRange(payload);
        return ev.ToArray();
    }

    private static byte[] RewrapRmid(byte[] original, byte[] newSmf)
    {
        var body = new List<byte>(newSmf.Length + 32);
        body.AddRange(Encoding.ASCII.GetBytes("RMID"));

        var p = 12;
        while (p + 8 <= original.Length)
        {
            var id = Encoding.ASCII.GetString(original, p, 4);
            var size = original[p + 4] | (original[p + 5] << 8) | (original[p + 6] << 16) | (original[p + 7] << 24);
            var contentStart = p + 8;
            if (size < 0 || contentStart + size > original.Length)
                size = original.Length - contentStart;

            var content = id == "data" ? newSmf : original[contentStart..(contentStart + size)];

            body.AddRange(Encoding.ASCII.GetBytes(id));
            WriteU32Le(body, (uint)content.Length);
            body.AddRange(content);
            if ((content.Length & 1) != 0) body.Add(0); // RIFF chunks are word-aligned

            p = contentStart + size + (size & 1);
        }

        var outp = new List<byte>(body.Count + 8);
        outp.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        WriteU32Le(outp, (uint)body.Count);
        outp.AddRange(body);
        return outp.ToArray();
    }

    // --- low-level helpers ---------------------------------------------------------------------

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

    private static void WriteVarLen(List<byte> outp, long value)
    {
        if (value < 0) value = 0;
        Span<byte> buf = stackalloc byte[5];
        var i = 0;
        buf[i++] = (byte)(value & 0x7F);
        value >>= 7;
        while (value > 0)
        {
            buf[i++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        for (var j = i - 1; j >= 0; j--)
            outp.Add(buf[j]);
    }

    private static uint ReadU32(byte[] data, int pos) =>
        (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);

    private static void WriteU32(List<byte> outp, uint value)
    {
        outp.Add((byte)(value >> 24));
        outp.Add((byte)(value >> 16));
        outp.Add((byte)(value >> 8));
        outp.Add((byte)value);
    }

    private static void WriteU32Le(List<byte> outp, uint value)
    {
        outp.Add((byte)value);
        outp.Add((byte)(value >> 8));
        outp.Add((byte)(value >> 16));
        outp.Add((byte)(value >> 24));
    }
}
