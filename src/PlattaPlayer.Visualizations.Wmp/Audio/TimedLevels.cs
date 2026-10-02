using System;

namespace PlattaPlayer.Visualizations.Wmp.Audio;

/// <summary>
/// Managed analogue of WMP's <c>TimedLevel</c> buffer: a per-frame snapshot of the two-channel
/// frequency spectrum and time-domain waveform that every renderer consumes (see analysis §0).
///
/// The original is fed pre-computed 1024-bin spectra / 1024-sample waveforms as bytes 0..255 and a
/// <c>state</c> flag (1 = stopped/paused → reuse the cached frame, 2 = fresh audio). Plugins fill it
/// from <see cref="TapToTimedLevel"/> via <see cref="LoadFrom"/>, the ONE adapter from our float tap to
/// WMP's bytes, shared with Bars and Waves exactly as WMP hands every effect the same block.
/// </summary>
public sealed class TimedLevels
{
    public const int Bins = 1024;

    /// <summary>Channel 0/1 spectrum, one byte 0..255 per FFT bin.</summary>
    public readonly byte[][] Frequency = { new byte[Bins], new byte[Bins] };

    /// <summary>Channel 0/1 time-domain samples, one byte 0..255 each (silence = 128).</summary>
    public readonly byte[][] Waveform = { new byte[Bins], new byte[Bins] };

    /// <summary>1 = stopped/paused (reuse the cached frame); 2 = fresh audio present.</summary>
    public int State;

    /// <summary>
    /// Copies one <see cref="TapToTimedLevel"/> frame. Its <c>State</c> carries over as-is: 2 is fresh
    /// audio, anything else holds the field still.
    ///
    /// This replaced a private float-to-byte conversion that encoded the spectrum LINEARLY
    /// (<c>magnitude × 255</c>). WMP's frequency bytes are LOGARITHMIC (wmp.dll expands a byte to
    /// <c>10^(8v/255)</c>), so a typical bass bin came out around a ninth of what WMP delivers. Alchemy's
    /// bass, the mean of six bins over 200, then sat near 0.1 instead of near 1. Nearly everything is
    /// scaled by it (disc radius and opacity, star radius, chord length), so the effect rendered small and
    /// dark, and the <c>bass &gt; 0.9</c> bass hit almost never fired.
    /// </summary>
    public void LoadFrom(TimedLevelFrame frame)
    {
        State = frame.State;
        frame.Frequency0.CopyTo(Frequency[0], 0);
        frame.Frequency1.CopyTo(Frequency[1], 0);
        frame.Waveform0.CopyTo(Waveform[0], 0);
        frame.Waveform1.CopyTo(Waveform[1], 0);
    }
}
