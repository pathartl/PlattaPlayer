namespace PlattaPlayer.Codecs.Midi;

/// <summary>
/// A MIDI message scheduled at an absolute time (seconds from the start). Channel messages are packed
/// for <c>midiOutShortMsg</c> (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16) in <see cref="Msg"/>; a SysEx
/// message instead carries its complete bytes (<c>F0 … F7</c>) in <see cref="SysEx"/> with <see cref="Msg"/> 0.
/// </summary>
internal readonly record struct ScheduledMessage(double Seconds, uint Msg, byte[]? SysEx = null)
{
    public bool IsSysEx => SysEx is not null;
}

/// <summary>
/// Minimal Standard MIDI File reader. Parses every track into channel and SysEx messages tagged with
/// absolute ticks, merges them in time order, walks the combined timeline applying tempo changes to
/// convert ticks → seconds, and returns the messages in time order. Non-tempo meta events are dropped.
/// RIFF-wrapped files (<c>.rmi</c>) are unwrapped.
/// </summary>
internal static class SmfParser
{
    private const double DefaultMicrosPerQuarter = 500_000; // 120 BPM

    private readonly record struct RawEvent(long Tick, int Order, bool IsTempo, int Tempo, uint Msg, byte[]? SysEx);

    public static (ScheduledMessage[] Events, double Duration) Parse(byte[] data)
    {
        var pos = UnwrapRmid(data);

        if (data.Length - pos < 14 || ReadChunkId(data, ref pos) != "MThd")
            return (Array.Empty<ScheduledMessage>(), 0);

        var headerLen = ReadUInt32(data, ref pos);
        var headerStart = pos;
        _ = ReadUInt16(data, ref pos);              // format (0/1/2) — handled uniformly by merging tracks
        int trackCount = ReadUInt16(data, ref pos);
        short division = (short)ReadUInt16(data, ref pos);
        pos = headerStart + (int)headerLen;         // tolerate larger-than-expected headers

        // Timing basis. Positive division = ticks per quarter note (tempo-driven). Negative =
        // SMPTE: high byte is -framesPerSecond, low byte is ticks per frame (tempo-independent).
        var smpte = division < 0;
        double ticksPerQuarter = smpte ? 0 : division;
        if (!smpte && ticksPerQuarter <= 0) ticksPerQuarter = 96; // guard malformed division
        double smpteTicksPerSecond = 0;
        if (smpte)
        {
            int framesPerSecond = -(division >> 8);
            int ticksPerFrame = division & 0xFF;
            smpteTicksPerSecond = framesPerSecond * ticksPerFrame;
            if (smpteTicksPerSecond <= 0) smpteTicksPerSecond = 24 * 4;
        }

        var raw = new List<RawEvent>(1024);
        var order = 0;

        for (var t = 0; t < trackCount && pos + 8 <= data.Length; t++)
        {
            if (ReadChunkId(data, ref pos) != "MTrk")
                break;

            var trackLen = (int)ReadUInt32(data, ref pos);
            var trackEnd = Math.Min(pos + trackLen, data.Length);
            ParseTrack(data, pos, trackEnd, raw, ref order);
            pos = trackEnd;
        }

        if (raw.Count == 0)
            return (Array.Empty<ScheduledMessage>(), 0);

        raw.Sort(static (a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));

        var events = new List<ScheduledMessage>(raw.Count);
        double seconds = 0;
        long lastTick = 0;
        var microsPerQuarter = DefaultMicrosPerQuarter;

        foreach (var ev in raw)
        {
            var deltaTicks = ev.Tick - lastTick;
            if (deltaTicks > 0)
            {
                seconds += smpte
                    ? deltaTicks / smpteTicksPerSecond
                    : deltaTicks * (microsPerQuarter / ticksPerQuarter) / 1_000_000.0;
                lastTick = ev.Tick;
            }

            if (ev.IsTempo)
            {
                if (!smpte && ev.Tempo > 0) microsPerQuarter = ev.Tempo;
            }
            else
            {
                events.Add(new ScheduledMessage(seconds, ev.Msg, ev.SysEx));
            }
        }

        return (events.ToArray(), seconds);
    }

    /// <summary>Returns the offset of the SMF image: past the RIFF <c>RMID</c> header's <c>data</c> chunk, or 0.</summary>
    private static int UnwrapRmid(byte[] data)
    {
        if (data.Length < 20 || ReadAscii(data, 0) != "RIFF" || ReadAscii(data, 8) != "RMID")
            return 0;

        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id = ReadAscii(data, pos);
            var size = BitConverter.ToInt32(data, pos + 4); // RIFF sizes are little-endian
            if (id == "data") return pos + 8;
            if (size < 0) break;
            pos += 8 + size + (size & 1);
        }
        return 0;
    }

    private static void ParseTrack(byte[] data, int start, int end, List<RawEvent> sink, ref int order)
    {
        var pos = start;
        long tick = 0;
        byte runningStatus = 0;

        // A SysEx split into an F0 packet plus F7 continuation packets, still waiting for its closing F7.
        List<byte>? pendingSysEx = null;
        long pendingTick = 0;

        while (pos < end)
        {
            tick += ReadVarLen(data, ref pos, end);
            if (pos >= end) break;

            var b = data[pos];

            if (b == 0xFF) // meta event
            {
                pos++;
                if (pos >= end) break;
                var metaType = data[pos++];
                var len = (int)ReadVarLen(data, ref pos, end);

                if (metaType == 0x51 && len == 3 && pos + 3 <= end)
                {
                    var tempo = (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2];
                    sink.Add(new RawEvent(tick, order++, IsTempo: true, tempo, 0, null));
                }

                pos += len;
                runningStatus = 0;
            }
            else if (b is 0xF0 or 0xF7) // SysEx (F0) or continuation / escape (F7)
            {
                pos++;
                var len = (int)ReadVarLen(data, ref pos, end);
                var count = Math.Clamp(end - pos, 0, len);
                var payload = new ReadOnlySpan<byte>(data, pos, count);
                pos += len;
                runningStatus = 0;

                if (b == 0xF0)
                {
                    // The SMF stores F0's payload without the leading F0.
                    pendingSysEx = new List<byte>(count + 1) { 0xF0 };
                    pendingSysEx.AddRange(payload);
                    pendingTick = tick;
                }
                else if (pendingSysEx is not null)
                {
                    pendingSysEx.AddRange(payload);
                }
                else if (count > 0 && payload[0] == 0xF0 && payload[^1] == 0xF7)
                {
                    // An F7 escape carrying one whole SysEx.
                    sink.Add(new RawEvent(tick, order++, IsTempo: false, 0, 0, payload.ToArray()));
                }

                if (pendingSysEx is { Count: > 1 } && pendingSysEx[^1] == 0xF7)
                {
                    sink.Add(new RawEvent(pendingTick, order++, IsTempo: false, 0, 0, pendingSysEx.ToArray()));
                    pendingSysEx = null;
                }
            }
            else // channel voice message (possibly using running status)
            {
                byte status;
                if ((b & 0x80) != 0) { status = b; pos++; runningStatus = status; }
                else { status = runningStatus; if (status == 0) { pos++; continue; } }

                var type = status & 0xF0;
                var dataBytes = type is 0xC0 or 0xD0 ? 1 : 2;
                if (pos + dataBytes > end) break;

                var d1 = data[pos++];
                byte d2 = 0;
                if (dataBytes == 2) d2 = data[pos++];

                var msg = (uint)status | ((uint)d1 << 8) | ((uint)d2 << 16);
                sink.Add(new RawEvent(tick, order++, IsTempo: false, 0, msg, null));
            }
        }
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

    private static string ReadAscii(byte[] data, int pos) => System.Text.Encoding.ASCII.GetString(data, pos, 4);

    private static string ReadChunkId(byte[] data, ref int pos)
    {
        var id = ReadAscii(data, pos);
        pos += 4;
        return id;
    }

    private static uint ReadUInt32(byte[] data, ref int pos)
    {
        var v = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        pos += 4;
        return v;
    }

    private static int ReadUInt16(byte[] data, ref int pos)
    {
        var v = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        return v;
    }
}
