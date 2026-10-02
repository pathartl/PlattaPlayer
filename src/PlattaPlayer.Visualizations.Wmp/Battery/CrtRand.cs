namespace PlattaPlayer.Visualizations.Wmp.Battery;

/// <summary>
/// The C runtime's <c>rand()</c>, which is the only random source Battery has.
///
/// Battery calls <c>_o_rand</c> straight off the CRT and uses the raw 0..32767 result in its own
/// arithmetic (<c>rand() % 15 + 1</c>, <c>(float)rand() / 32767.0f</c>, ...). It shares one stream
/// across every class, including one draw per pixel inside CSwirlShift's warp. So the port does not take
/// a <see cref="Random"/>. It takes this class, which yields the identical integers, and the harness can
/// subclass it to replay a scripted stream.
///
/// The default stream is the Microsoft CRT LCG:
/// <c>state = state * 214013 + 2531011; return (state >> 16) &amp; 0x7FFF</c>.
/// It is seeded the way Battery's creator seeds it with <c>srand</c>.
/// </summary>
public class CrtRand
{
    /// <summary>C's <c>RAND_MAX</c>.</summary>
    public const int Max = 32767;

    private uint _state;

    public CrtRand(uint seed = 1) => _state = seed;

    /// <summary>Draws counted since construction. Comparing draw counts catches control-flow bugs on their own.</summary>
    public long Draws { get; private set; }

    /// <summary><c>srand(seed)</c>.</summary>
    public void Seed(uint seed) => _state = seed;

    /// <summary>One <c>rand()</c>: 0..32767.</summary>
    public int Next()
    {
        Draws++;
        return NextCore();
    }

    protected virtual int NextCore()
    {
        _state = unchecked(_state * 214013u + 2531011u);
        return (int)((_state >> 16) & 0x7FFF);
    }

    /// <summary><c>(float)rand() / 32767.0f</c>, the unit draw every Randomize uses, in SINGLE precision
    /// (the divisor is the f32 literal at 0x18088c668).</summary>
    public float Unit() => Next() / 32767.0f;
}
