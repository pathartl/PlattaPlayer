using System;

namespace PlattaPlayer.Visualizations.MilkDrop.Expressions;

/// <summary>The intrinsic functions of the MilkDrop/ns-eel expression language.</summary>
public enum EelFunc
{
    Sin, Cos, Tan, Asin, Acos, Atan, Atan2,
    Sqr, Sqrt, InvSqrt, Pow, Exp, Log, Log10,
    Abs, Sign, Min, Max, Floor, Ceil, Int, Frac, FMod,
    Rand, Sigmoid, Bnot, Band, Bor, Equal, Above, Below,
    // Special forms below evaluate their arguments lazily / repeatedly.
    If, Exec2, Exec3, Loop, While
}

/// <summary>A call to one of the <see cref="EelFunc"/> intrinsics.</summary>
public sealed class CallExpr(EelFunc func, Expr[] args) : Expr
{
    public override double Eval(EvalContext ctx)
    {
        // Special forms first: these must not eagerly evaluate every argument.
        switch (func)
        {
            case EelFunc.If:
                return args[0].Eval(ctx) != 0.0 ? args[1].Eval(ctx) : args[2].Eval(ctx);
            case EelFunc.Exec2:
                args[0].Eval(ctx);
                return args[1].Eval(ctx);
            case EelFunc.Exec3:
                args[0].Eval(ctx);
                args[1].Eval(ctx);
                return args[2].Eval(ctx);
            case EelFunc.Loop:
            {
                var n = (int)args[0].Eval(ctx);
                var last = 0.0;
                for (var i = 0; i < n && i < 1_000_000; i++) last = args[1].Eval(ctx);
                return last;
            }
            case EelFunc.While:
            {
                var last = 0.0;
                var guard = 0;
                while (args[0].Eval(ctx) != 0.0 && guard++ < 1_000_000) last = args[0].Eval(ctx);
                return last;
            }
        }

        var a = args.Length > 0 ? args[0].Eval(ctx) : 0.0;
        var b = args.Length > 1 ? args[1].Eval(ctx) : 0.0;
        return func switch
        {
            EelFunc.Sin => Math.Sin(a),
            EelFunc.Cos => Math.Cos(a),
            EelFunc.Tan => Math.Tan(a),
            EelFunc.Asin => Math.Asin(a),
            EelFunc.Acos => Math.Acos(a),
            EelFunc.Atan => Math.Atan(a),
            EelFunc.Atan2 => Math.Atan2(a, b),
            EelFunc.Sqr => a * a,
            EelFunc.Sqrt => Math.Sqrt(Math.Abs(a)),
            EelFunc.InvSqrt => a == 0.0 ? 0.0 : 1.0 / Math.Sqrt(Math.Abs(a)),
            EelFunc.Pow => Math.Pow(a, b),
            EelFunc.Exp => Math.Exp(a),
            EelFunc.Log => a <= 0.0 ? 0.0 : Math.Log(a),
            EelFunc.Log10 => a <= 0.0 ? 0.0 : Math.Log10(a),
            EelFunc.Abs => Math.Abs(a),
            EelFunc.Sign => Math.Sign(a),
            EelFunc.Min => Math.Min(a, b),
            EelFunc.Max => Math.Max(a, b),
            EelFunc.Floor => Math.Floor(a),
            EelFunc.Ceil => Math.Ceiling(a),
            EelFunc.Int => Math.Truncate(a),
            EelFunc.Frac => a - Math.Floor(a),
            EelFunc.FMod => b == 0.0 ? 0.0 : a % b,
            EelFunc.Rand => Math.Floor(ctx.Rng.NextDouble() * (a <= 0.0 ? 1.0 : a)),
            EelFunc.Sigmoid => 1.0 / (1.0 + Math.Exp(-a * b)),
            EelFunc.Bnot => a == 0.0 ? 1.0 : 0.0,
            EelFunc.Band => a != 0.0 && b != 0.0 ? 1.0 : 0.0,
            EelFunc.Bor => a != 0.0 || b != 0.0 ? 1.0 : 0.0,
            EelFunc.Equal => a == b ? 1.0 : 0.0,
            EelFunc.Above => a > b ? 1.0 : 0.0,
            EelFunc.Below => a < b ? 1.0 : 0.0,
            _ => 0.0
        };
    }

    /// <summary>Maps a (lowercased) function name to its intrinsic, or null if unknown.</summary>
    public static EelFunc? Lookup(string name) => name switch
    {
        "sin" => EelFunc.Sin,
        "cos" => EelFunc.Cos,
        "tan" => EelFunc.Tan,
        "asin" => EelFunc.Asin,
        "acos" => EelFunc.Acos,
        "atan" => EelFunc.Atan,
        "atan2" => EelFunc.Atan2,
        "sqr" => EelFunc.Sqr,
        "sqrt" => EelFunc.Sqrt,
        "invsqrt" => EelFunc.InvSqrt,
        "pow" => EelFunc.Pow,
        "exp" => EelFunc.Exp,
        "log" => EelFunc.Log,
        "log10" => EelFunc.Log10,
        "abs" => EelFunc.Abs,
        "sign" => EelFunc.Sign,
        "min" => EelFunc.Min,
        "max" => EelFunc.Max,
        "floor" => EelFunc.Floor,
        "ceil" => EelFunc.Ceil,
        "int" => EelFunc.Int,
        "frac" => EelFunc.Frac,
        "fmod" => EelFunc.FMod,
        "rand" => EelFunc.Rand,
        "sigmoid" => EelFunc.Sigmoid,
        "bnot" => EelFunc.Bnot,
        "band" => EelFunc.Band,
        "bor" => EelFunc.Bor,
        "equal" => EelFunc.Equal,
        "above" => EelFunc.Above,
        "below" => EelFunc.Below,
        "if" => EelFunc.If,
        "exec2" => EelFunc.Exec2,
        "exec3" => EelFunc.Exec3,
        "loop" => EelFunc.Loop,
        "while" => EelFunc.While,
        _ => null
    };

    /// <summary>Expected argument count, or -1 when variadic/unchecked.</summary>
    public static int Arity(EelFunc f) => f switch
    {
        EelFunc.Atan2 or EelFunc.Pow or EelFunc.Min or EelFunc.Max or EelFunc.FMod
            or EelFunc.Sigmoid or EelFunc.Band or EelFunc.Bor or EelFunc.Equal
            or EelFunc.Above or EelFunc.Below or EelFunc.Exec2 or EelFunc.Loop or EelFunc.While => 2,
        EelFunc.If or EelFunc.Exec3 => 3,
        _ => 1
    };
}
