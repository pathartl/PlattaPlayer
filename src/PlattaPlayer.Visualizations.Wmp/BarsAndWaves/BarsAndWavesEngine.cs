using PlattaPlayer.Visualizations.Wmp.Audio;

namespace PlattaPlayer.Visualizations.Wmp.BarsAndWaves;

/// <summary>
/// A faithful reimplementation of Windows Media Player's "Bars and Waves" visualization
/// ({48501FF0-F6A9-11D2-9435-00A0C92A2F2D}, internal to wmp.dll).
///
/// Every constant and every branch here was read out of the decompiled effect and is verified frame by
/// frame against captures taken from the real object by the harness in <c>tools/PlattaPlayer.Wmp.Harness</c>.
/// Unlike Alchemy and Battery, this effect has NO fixed internal field: it rasterises directly at the
/// destination size and presents 1:1, so the engine renders at whatever size the host gives it.
/// </summary>
public sealed class BarsAndWavesEngine
{
    private const int MaxBars = 2048;
    private const int RingLength = 16;
    private const int RingMask = RingLength - 1;

    private readonly BarsAndWavesConfig _config = new();
    private readonly DibSurface _surface = new();
    private readonly SpectrumBands _bands = new();

    private readonly int[] _barValue = new int[MaxBars];
    private readonly float[] _barVelocity = new float[MaxBars];
    private readonly int[] _peakValue = new int[MaxBars];
    private readonly float[] _peakVelocity = new float[MaxBars];
    private readonly int[] _peakHold = new int[MaxBars];

    /// <summary>Row-major history of bar tops: row 2i is bar i, row 2i+1 is its peak. Entries start at -1.</summary>
    private readonly int[] _trail = new int[MaxBars * 2 * RingLength];

    private readonly int[] _barRamp = new int[RingLength];
    private readonly int[] _peakRamp = new int[RingLength];
    private readonly byte[] _levels0 = new byte[TimedLevelFrame.Bins];
    private readonly byte[] _levels1 = new byte[TimedLevelFrame.Bins];

    private int _ringHead;
    private int _trailRows;
    private bool _ringActive;
    private bool _needsFullClear = true;
    private int _mappedBackground;
    private long _lastTimeStamp = long.MinValue;

    /// <summary>
    /// Creates the engine in the state a freshly constructed wmp.dll effect object is in — constructor
    /// defaults, no preset applied.
    ///
    /// It deliberately does NOT pre-apply a preset. Several config fields are sticky (no preset writes
    /// the peak-hold count except Bars, and Scope never writes the secondary colour), so applying one
    /// here would leak its values into whichever preset the host selects next. Doing exactly that made
    /// Ocean Mist inherit Bars' 4-frame peak hold and lag its peak caps by four frames. The host is
    /// expected to call <see cref="SetPreset"/>, which is what WMP does.
    /// </summary>
    public BarsAndWavesEngine()
    {
        Array.Fill(_trail, -1);
        RebuildRamps();
    }

    public BarsAndWavesPreset Preset { get; private set; } = BarsAndWavesPreset.Bars;

    public int Width => _surface.Width;
    public int Height => _surface.Height;

    /// <summary>Live BGRA frame, <see cref="Width"/> x <see cref="Height"/>.</summary>
    public int[] FrameBuffer => _surface.Pixels;

    public static string TitleOf(BarsAndWavesPreset preset) => preset switch
    {
        BarsAndWavesPreset.Bars => "Bars",
        BarsAndWavesPreset.OceanMist => "Ocean Mist",
        BarsAndWavesPreset.FireStorm => "Fire Storm",
        _ => "Scope",
    };

    public string CurrentName => TitleOf(Preset);

    /// <summary>
    /// Selects a preset. Mirrors <c>SetCurrentPreset</c>, which also clears the bar and peak state and
    /// leaves untouched whatever fields that preset does not write.
    /// </summary>
    public void SetPreset(BarsAndWavesPreset preset)
    {
        Preset = preset;
        _config.Apply(preset);
        Array.Clear(_barValue);
        Array.Clear(_barVelocity);
        Array.Clear(_peakValue);
        Array.Clear(_peakVelocity);
        Array.Clear(_peakHold);
        RebuildRamps();
    }

    public void Resize(int width, int height)
    {
        if (width == _surface.Width && height == _surface.Height) return;
        _surface.Resize(width, height);
        // A size change in the original destroys and recreates the DIB, which clears the trail history
        // and rebuilds the colour ramps before anything is drawn.
        ClearRing();
        RebuildRamps();
        _surface.Clear(_mappedBackground);
        _needsFullClear = false;
    }

    /// <summary>Advance one frame.</summary>
    public void Render(TimedLevelFrame frame)
    {
        var w = _surface.Width;
        var h = _surface.Height;
        if (w < 1 || h < 1) return;

        if (frame.State == TimedLevelFrame.StateStopped)
        {
            _surface.Clear(_mappedBackground);
            return;
        }

        // The original skips its entire redraw when paused OR when the audio timestamp has not moved,
        // re-presenting the previous surface instead. Without this a host ticking faster than the audio
        // ring advances would animate the falloff and trails at the wrong rate.
        if (frame.State == TimedLevelFrame.StatePaused || frame.TimeStamp == _lastTimeStamp) return;

        ApplyTrailPolicy();

        switch (_config.Style)
        {
            case 0: RenderBars(frame, 20); break;
            case 1: RenderBars(frame, 50); break;
            case 2: RenderBars(frame, 1024); break;
            case 3: RenderScope(frame); break;
            // Style 4 (a per-sample dot scope) exists in the original but no preset selects it, so
            // there is no ground truth for it and nothing is invented here.
        }

        _lastTimeStamp = frame.TimeStamp;
    }

    // ---- bars ------------------------------------------------------------------------------------

    private void RenderBars(TimedLevelFrame frame, int maxBars)
    {
        var w = _surface.Width;
        var h = _surface.Height;

        var barWidth = _config.BarWidth;
        if (barWidth == 0)
        {
            barWidth = (w - (maxBars - 1) * _config.Gap) / maxBars;
            if (barWidth < 1) barWidth = 1;
        }

        var bars = w / (_config.Gap + barWidth) + 1;
        if (bars > maxBars) bars = maxBars;

        var slack = w - (_config.Gap + barWidth) * bars;
        if (slack < 0) slack = 0;

        _bands.Ensure(bars, maxBars);
        if (bars > 0x400) bars = 0x400;

        _bands.Accumulate(frame.Frequency0, _levels0, bars);
        var stereo = frame.ChannelCount > 1;
        if (stereo) _bands.Accumulate(frame.Frequency1, _levels1, bars);

        _trailRows = bars * 2;

        for (var i = 0; i < bars; i++)
        {
            var height = ScaleLevel(_levels0[i], h);
            if (height > 0 && frame.State == TimedLevelFrame.StatePlaying) height += Jitter();

            if (stereo)
            {
                var right = ScaleLevel(_levels1[i], h);
                if (right > 0 && frame.State == TimedLevelFrame.StatePlaying) right += Jitter();
                if (height <= right) height = right;
            }

            height = AdvanceBar(height, i);

            var x0 = (_config.Gap + barWidth) * i + (slack >> 1);
            var x1 = x0 + barWidth;
            if (x0 + barWidth - 1 >= w - 1) x1 = w;

            var barTop = DrawBar(i * 2, x0, x1 - 1, height);

            if (_config.ShowPeaks)
            {
                var peak = AdvancePeak(height, i);
                DrawPeak(i * 2 + 1, x0, x1 - 1, peak, barTop);
            }
        }
    }

    private int ScaleLevel(byte level, int h) => (int)((float)(level * h) / 255.0f * _config.HeightScale);

    /// <summary>
    /// The +/-10 px per-bar wobble the original adds on fresh frames. wmp.dll statically links its CRT,
    /// so its <c>rand()</c> stream is unreachable from this process and cannot be matched value for
    /// value; the harness therefore drives both sides with a non-2 "fresh" state, which takes this
    /// branch out of play and allows a bit-exact comparison of everything else.
    /// </summary>
    private int Jitter() => Random.Shared.Next(0, 0x8000) * 0x14 / 0x7FFF - 10;

    /// <summary>
    /// Bar falloff. Note the asymmetry, which is faithful: rising snaps to the new height and resets the
    /// velocity, while falling stores the decremented value but RETURNS the pre-decrement one, so the
    /// drawn bar lags the stored state by a frame.
    /// </summary>
    private int AdvanceBar(int newHeight, int index)
    {
        var current = _barValue[index];
        if (current < newHeight)
        {
            _barValue[index] = newHeight;
            _barVelocity[index] = _config.FallSpeed;
            return newHeight;
        }

        var velocity = _barVelocity[index];
        _barValue[index] = current - (int)velocity;
        _barVelocity[index] = velocity + _config.FallAccel;
        return current < 0 ? 0 : current;
    }

    private int AdvancePeak(int newHeight, int index)
    {
        var current = _peakValue[index];
        if (current < newHeight)
        {
            _peakValue[index] = newHeight;
            if (newHeight < 1)
            {
                _peakVelocity[index] = 0f;
            }
            else
            {
                _peakVelocity[index] = _config.PeakInitialVelocity;
                _peakHold[index] = 0;
            }
            return newHeight;
        }

        if (_peakHold[index] > _config.PeakHoldFrames)
        {
            var velocity = _peakVelocity[index];
            _peakValue[index] = current - (int)velocity;
            _peakVelocity[index] = velocity + _config.PeakAccel;
            return current < 0 ? 0 : current;
        }

        _peakHold[index]++;
        return current;
    }

    /// <summary>
    /// Draws one bar, and returns the topmost row it covered so the peak cap can be floored above it.
    ///
    /// With trails active this walks the 16-frame history forward from the ring head — newest first —
    /// painting each entry only where it rises ABOVE everything already painted. That is what produces
    /// the trail: when a bar falls, the taller history above it survives in progressively faded colours.
    /// </summary>
    private int DrawBar(int row, int x0, int x1, int height)
    {
        var h = _surface.Height;
        var value = height > 0 ? height : -1;
        var top = value < h - 1 ? value : h - 2;

        if (!_ringActive)
        {
            if (top >= 0) _surface.FillRect(x0, h - top, x1, h, _barRamp[0]);
            return top;
        }

        var slot = _ringHead;
        _trail[row * RingLength + slot] = top;

        var covered = 0;
        for (var k = 0; k < RingLength; k++)
        {
            var colour = _barRamp[k];
            if (colour == _mappedBackground) break;

            var entry = _trail[row * RingLength + slot];
            if (covered <= entry)
            {
                _surface.FillRect(x0, h - entry, x1, h - covered, colour);
                covered = entry + 1;
            }

            slot = (slot + 1) & RingMask;
            if (slot == _ringHead) break;
        }
        return covered - 1;
    }

    /// <summary>
    /// Draws the peak cap. With trails active the history is painted oldest-first so the newest cap ends
    /// up on top, and entries that have drifted off the surface are retired to -1.
    /// </summary>
    private void DrawPeak(int row, int x0, int x1, int peak, int barTop)
    {
        var h = _surface.Height;
        var floor = barTop > 0 ? barTop : 1;
        var top = peak < h ? peak : -1;

        if (!_ringActive)
        {
            if (floor <= top) _surface.HLine(x0, x1, h - top, _peakRamp[0]);
            return;
        }

        var slot = _ringHead;
        _trail[row * RingLength + slot] = top;

        for (var k = RingLength - 1; k >= 0; k--)
        {
            var colour = _peakRamp[k];
            slot = (slot - 1) & RingMask;

            var index = row * RingLength + slot;
            var entry = _trail[index];
            if (entry >= h) { _trail[index] = -1; entry = -1; }

            if (colour != _mappedBackground && floor <= entry)
                _surface.HLine(x0, x1, h - entry, colour);

            if (slot == _ringHead) break;
        }
    }

    // ---- scope -----------------------------------------------------------------------------------

    private void RenderScope(TimedLevelFrame frame)
    {
        var w = _surface.Width;
        var h = _surface.Height;
        var size = Math.Min(w, h);
        var baseline = (h >> 1) - (size >> 2);
        var amplitude = (size >> 1) * 0.00390625f; // (size/2) / 256

        var wave = frame.Waveform0;
        var previousY = (int)(wave[0] * amplitude) + baseline;
        var colour = _barRamp[0];

        if (w <= 0x400)
        {
            // The sample cursor is ACCUMULATED in float rather than computed per column; the rounding
            // that produces is part of the shape.
            var cursor = 0.0f;
            var step = 1024.0f / w * 0.0009765625f;
            for (var x = 1; x < w; x++)
            {
                cursor += step;
                var index = (int)(cursor * 1024.0f);
                if (index > 0x3FF) index = 0x3FF;
                var y = (int)(wave[index] * amplitude) + baseline;
                _surface.Line(x - 1, previousY, x, y, colour);
                previousY = y;
            }
        }
        else
        {
            var previousX = 0;
            for (var i = 1; i < 0x400; i++)
            {
                var x = (int)(i * 0.0009765625f * w);
                var y = (int)(wave[i] * amplitude) + baseline;
                _surface.Line(previousX, previousY, x, y, colour);
                previousX = x;
                previousY = y;
            }
        }
    }

    // ---- trails and colour ramps -----------------------------------------------------------------

    private void ApplyTrailPolicy()
    {
        if (_needsFullClear)
        {
            ClearRing();
            RebuildRamps();
            _surface.Clear(_mappedBackground);
            _needsFullClear = false;
            return;
        }

        if (_config.FadeStep != 0xFF && _config.TrailMode != 0)
        {
            var shift = _config.TrailMode switch
            {
                1 or 2 or 3 => 1, // drift: the history slides down a pixel per frame
                4 => 0,           // freeze: the history holds its height and only fades
                _ => -1,
            };
            if (shift >= 0)
            {
                ShiftTrails(shift);
                return;
            }
        }

        _surface.Clear(_mappedBackground);
    }

    private void ShiftTrails(int shift)
    {
        if (shift != 0)
        {
            var h = _surface.Height;
            var end = _trailRows * RingLength;
            for (var i = 0; i < end; i++)
            {
                var v = _trail[i];
                if (v >= 0 && v < h) _trail[i] = v - shift;
            }
        }

        _ringActive = true;
        _ringHead = (_ringHead - 1) & RingMask;
        _surface.Clear(_mappedBackground);
    }

    private void ClearRing()
    {
        Array.Fill(_trail, -1);
        _trailRows = 0;
        _ringActive = false;
    }

    /// <summary>
    /// Builds the two 16-entry ramps: entry 0 is the full colour and each subsequent entry has been
    /// stepped one more <c>FadeStep</c> toward the background.
    /// </summary>
    private void RebuildRamps()
    {
        var primary = _config.Primary;
        var secondary = _config.Secondary;

        for (var k = 0; k < RingLength; k++)
        {
            _barRamp[k] = DibSurface.FromColorRef(primary);
            _peakRamp[k] = DibSurface.FromColorRef(secondary);
            primary = FadeToward(primary, _config.Background, _config.FadeStep);
            secondary = FadeToward(secondary, _config.Background, _config.FadeStep);
        }

        _mappedBackground = DibSurface.FromColorRef(_config.Background);
    }

    /// <summary>Per-channel clamped linear step toward the background; never overshoots.</summary>
    private static int FadeToward(int colour, int background, int step)
    {
        var result = 0;
        for (var shift = 0; shift <= 16; shift += 8)
        {
            var c = (colour >> shift) & 0xFF;
            var b = (background >> shift) & 0xFF;
            var v = c < b ? Math.Min(b, c + step) : Math.Max(b, c - step);
            result |= (v & 0xFF) << shift;
        }
        return result;
    }
}
