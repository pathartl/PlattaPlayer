namespace PlattaPlayer.Visualizations.MilkDrop.Expressions;

/// <summary>
/// A compiled block of MilkDrop equations (e.g. the whole per-frame block) ready to execute against an
/// <see cref="EvalContext"/>. Parsing happens once when the preset loads; <see cref="Execute"/> runs on
/// the hot path (per frame, or per mesh cell for per-pixel code).
/// </summary>
public sealed class EelProgram
{
    private readonly Expr _root;

    private EelProgram(Expr root) => _root = root;

    /// <summary>An empty program that does nothing — used for absent equation blocks.</summary>
    public static EelProgram Empty { get; } = new(new BlockExpr([]));

    /// <summary>True when this is the do-nothing program (no source compiled), so callers can skip it
    /// on hot paths such as the per-vertex per-pixel loop.</summary>
    public bool IsEmpty => ReferenceEquals(this, Empty);

    /// <summary>Parses <paramref name="source"/>, resolving identifiers against <paramref name="vars"/>.</summary>
    public static EelProgram Compile(string? source, VariableTable vars)
        => string.IsNullOrWhiteSpace(source) ? Empty : new EelProgram(Parser.Parse(source, vars));

    public void Execute(EvalContext ctx) => _root.Eval(ctx);
}
