using PlattaPlayer.Visualizations.Wmp.Battery.Presets;
using PlattaPlayer.Visualizations.Wmp.Battery.Shifts;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Gpu;

/// <summary>
/// The warp tables at DEVICE resolution, for scales other than 1.
///
/// The field's own tables are still built by <see cref="BatteryCore"/>, three rows a frame as in the
/// original, because CSwirlShift's build draws from rand() and everything downstream depends on that
/// stream. The device table is a separate build from a clone of the shift
/// (<see cref="ShiftTable.CloneForDevice"/>): the same class with its pixel-sized parameters scaled, run
/// over the device grid in double precision.
///
/// A shift becomes pending (<see cref="BatteryPreset.Next"/>) at least 96 frames before it goes live, so
/// <see cref="Poll"/> starts its build on a background task as soon as it appears, and it is normally
/// ready long before it is needed. A shift that goes live without having been pending (a saved preset,
/// the first frame, a resize) is built on the spot. Tables are keyed by object and
/// <see cref="MemoryEffect.Version"/>, so a re-randomized pool entry is a new table.
///
/// Each entry is packed for upload as RGBA8, source x in R·256 + G and source y in B·256 + A, device row
/// 0 (field row 0) first.
/// </summary>
public sealed class DeviceShiftTables : IDisposable
{
    private readonly List<Entry> _entries = [];
    private readonly CancellationTokenSource _cancel = new();

    public DeviceShiftTables(BatteryFieldScale scale) => Scale = scale;

    public BatteryFieldScale Scale { get; }

    /// <summary>Entries alive now (harness: memory and timing).</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Call after each <see cref="BatteryCore.Render(BatteryLevels)"/>: start building the pending shift
    /// (and, for its transition, the live one it will fade from), and drop tables nothing refers to.
    /// </summary>
    public void Poll(BatteryPreset preset)
    {
        Entry? next = null;
        if (preset.Next is { } pending)
        {
            next = Find(pending, pending.Version) ?? Start(pending, background: true);
            if (next.Old is null && pending.Last is { } last)
                next.Old = Find(last, last.Version) ?? Start(last, background: true);
        }

        var live = preset.TransformTable is { } t ? Find(t, t.Version) : null;
        _entries.RemoveAll(e => e != next && e != live && e != next?.Old && e != live?.Old);
    }

    /// <summary>
    /// The device table for a recorded gather, building it now if it was never started. With
    /// <paramref name="withOld"/> (a transition step) the table it fades from is resolved too, when known.
    /// </summary>
    public Entry Resolve(ShiftTable table, int version, bool withOld)
    {
        var e = Find(table, version) ?? Start(table, background: false);
        if (withOld && e.Old is null && table.Last is { } last)
            e.Old = Find(last, last.Version) ?? Start(last, background: false);
        return e;
    }

    public void Dispose() => _cancel.Cancel();

    private Entry? Find(ShiftTable shift, int version)
    {
        foreach (var e in _entries)
            if (ReferenceEquals(e.Shift, shift) && e.Version == version) return e;
        return null;
    }

    private Entry Start(ShiftTable shift, bool background)
    {
        var s = Scale;
        // The clone is taken here, on the engine's thread, so the build never sees a later Randomize.
        var clone = shift.CloneForDevice(s.Scale, s.DeviceWidth, s.DeviceHeight);
        var token = _cancel.Token;
        var build = background
            ? Task.Run(() => Build(clone, token), token)
            : Task.FromResult(Build(clone, token));
        var remainder = shift is LinearShift ? LinearShift.DeviceScrollRemainder(clone) : (0.0, 0.0);
        var e = new Entry(shift, shift.Version, build, remainder);
        _entries.Add(e);
        return e;
    }

    /// <summary>Every device pixel's source, in parallel by rows.</summary>
    public static byte[] Build(ShiftTable clone, CancellationToken token = default)
    {
        int w = clone.W, h = clone.H;
        var rgba = new byte[w * h * 4];
        Parallel.For(0, h, new ParallelOptions { CancellationToken = token }, row =>
        {
            var o = row * w * 4;
            for (var col = 0; col < w; col++, o += 4)
            {
                var (x, y) = clone.DeviceSource(col, row);
                rgba[o] = (byte)(x >> 8);
                rgba[o + 1] = (byte)x;
                rgba[o + 2] = (byte)(y >> 8);
                rgba[o + 3] = (byte)y;
            }
        });
        return rgba;
    }

    public sealed class Entry
    {
        private readonly Task<byte[]> _build;

        internal Entry(ShiftTable shift, int version, Task<byte[]> build, (double X, double Y) scroll)
        {
            Shift = shift;
            Version = version;
            _build = build;
            ScrollRemainder = scroll;
        }

        public ShiftTable Shift { get; }

        public int Version { get; }

        /// <summary>The live table a transition into this one fades from, when there is one.</summary>
        public Entry? Old { get; internal set; }

        /// <summary>CLinearShift only: the fraction of a device pixel per frame the table's whole-pixel scroll
        /// leaves out.</summary>
        public (double X, double Y) ScrollRemainder { get; }

        public bool IsReady => _build.IsCompleted;

        /// <summary>The packed table. Blocks until the build finishes.</summary>
        public byte[] Rgba => _build.GetAwaiter().GetResult();
    }
}
