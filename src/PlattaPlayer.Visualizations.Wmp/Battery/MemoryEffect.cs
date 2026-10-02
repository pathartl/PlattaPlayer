namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// <c>CMemoryEffect</c>, the base of every Battery shift and render effect: a class name, eight double
/// parameters (registry <c>dbl1..dbl8</c>, object +0x08..+0x40) and a flags word (+0xe0).
///
/// A preset loads the parameters verbatim (<see cref="SetParams"/>). Random mode draws them
/// (<see cref="SetRandom"/>). Both then mark the effect dirty, which for a shift means its lookup table
/// is rebuilt.
/// </summary>
public abstract class MemoryEffect
{
    /// <summary>dbl1..dbl8. Indexed from zero here, so <c>P[0]</c> is the registry's <c>dbl1</c>.</summary>
    public double[] P { get; } = new double[8];

    /// <summary>The registry class name, e.g. <c>CTileShift</c>.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// +0xe0. A pre-effect is skipped for a frame when <c>(preEffect.Flags &amp; shift.Flags) != 0</c>.
    /// Of the shifts only CTileShift sets it (1).
    /// </summary>
    public int Flags { get; protected init; }

    /// <summary>
    /// Bumped whenever the parameters may have changed (<see cref="SetRandom"/>, <see cref="SetParams"/>, a
    /// preset load). Not part of the original: the GPU path keys its device-resolution warp tables on it.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>vtable slot 3.</summary>
    public abstract void Randomize(CrtRand rand);

    /// <summary>vtable slot 2.</summary>
    public virtual void MarkDirty()
    {
    }

    /// <summary>Slot 0 (<c>0x1804133f0</c>): Randomize, then MarkDirty.</summary>
    public void SetRandom(CrtRand rand)
    {
        Randomize(rand);
        MarkChanged();
    }

    /// <summary>Slot 1 (<c>0x180413230</c>): store all eight unchanged, then MarkDirty.</summary>
    public void SetParams(ReadOnlySpan<double> p)
    {
        for (var i = 0; i < 8; i++) P[i] = i < p.Length ? p[i] : 0.0;
        MarkChanged();
    }

    /// <summary>MarkDirty after the parameters were written.</summary>
    public void MarkChanged()
    {
        Version++;
        MarkDirty();
    }
}
