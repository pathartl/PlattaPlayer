namespace PlattaPlayer.Wmp.Harness.Synth;

/// <summary>
/// A <see cref="Random"/> that returns a fixed script of integers with C <c>rand()</c> semantics, so our
/// managed effects can be driven by the SAME draw sequence as the real DLL.
///
/// The mapping is the one mpvis's own code uses on every <c>rand()</c> result:
/// <list type="bullet">
/// <item><c>rand() % n</c> is written <c>Next(n)</c> in our port, so <see cref="Next(int)"/> returns
/// <c>script[i] % n</c>.</item>
/// <item><c>(double)rand() / 32767.0</c> is written <c>NextDouble()</c>, so <see cref="NextDouble"/>
/// returns <c>script[i] / 32767.0</c>.</item>
/// </list>
/// With <see cref="Interop.RandRedirect"/> feeding the same array to the DLL, both implementations see
/// identical draws — so any difference in the resulting parameters is a real difference in the
/// randomizer, and any difference in the warp output with matching parameters is a real difference in the
/// maths. Separating those two is the whole point: they look identical in a rendered frame.
///
/// <see cref="Draws"/> also makes the DRAW COUNT comparable, which catches a class of bug nothing else
/// does — a randomizer that reads its tunables in a different order still consumes the same number of
/// values and still produces plausible output, but every parameter is wrong.
/// </summary>
internal sealed class ScriptedRandom(int[] script) : Random
{
    private int _index;

    /// <summary>How many values have been consumed — compare against the DLL's own count.</summary>
    public int Draws => _index;

    public void Rewind() => _index = 0;

    private int Raw()
    {
        if (script.Length == 0) return 0;
        var value = script[_index % script.Length];
        _index++;
        return value;
    }

    public override int Next() => Raw();

    public override int Next(int maxValue) => maxValue <= 0 ? 0 : Raw() % maxValue;

    public override int Next(int minValue, int maxValue) =>
        maxValue <= minValue ? minValue : minValue + Raw() % (maxValue - minValue);

    public override double NextDouble() => Raw() / (double)Interop.RandRedirect.RandMax;

    protected override double Sample() => NextDouble();
}
