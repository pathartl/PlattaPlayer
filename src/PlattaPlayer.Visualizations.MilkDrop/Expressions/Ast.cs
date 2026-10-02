using System;

namespace PlattaPlayer.Visualizations.MilkDrop.Expressions;

/// <summary>Base class for every node in a parsed equation expression tree.</summary>
public abstract class Expr
{
    public abstract double Eval(EvalContext ctx);
}

/// <summary>An expression that can appear on the left of an assignment.</summary>
public interface IAssignable
{
    void Assign(EvalContext ctx, double value);
}

public sealed class NumberExpr(double value) : Expr
{
    public override double Eval(EvalContext ctx) => value;
}

public sealed class VarExpr(int slot) : Expr, IAssignable
{
    public override double Eval(EvalContext ctx) => ctx.GetVar(slot);
    public void Assign(EvalContext ctx, double value) => ctx.SetVar(slot, value);
}

/// <summary><c>megabuf(i)</c> / <c>gmegabuf(i)</c> access; readable and assignable.</summary>
public sealed class MemoryExpr(Expr index, bool global) : Expr, IAssignable
{
    public override double Eval(EvalContext ctx)
        => EvalContext.ReadMemory(global ? ctx.GlobalMemory : ctx.LocalMemory, index.Eval(ctx));

    public void Assign(EvalContext ctx, double value)
        => EvalContext.WriteMemory(global ? ctx.GlobalMemory : ctx.LocalMemory, index.Eval(ctx), value);
}

/// <summary><c>base[offset]</c> indexing into per-instance memory (ns-eel semantics: base + offset).</summary>
public sealed class IndexExpr(Expr baseExpr, Expr offset) : Expr, IAssignable
{
    public override double Eval(EvalContext ctx)
        => EvalContext.ReadMemory(ctx.LocalMemory, baseExpr.Eval(ctx) + offset.Eval(ctx));

    public void Assign(EvalContext ctx, double value)
        => EvalContext.WriteMemory(ctx.LocalMemory, baseExpr.Eval(ctx) + offset.Eval(ctx), value);
}

public sealed class UnaryExpr(char op, Expr operand) : Expr
{
    public override double Eval(EvalContext ctx)
    {
        var v = operand.Eval(ctx);
        return op switch
        {
            '-' => -v,
            '!' => v == 0.0 ? 1.0 : 0.0,
            _ => v
        };
    }
}

public sealed class BinaryExpr(string op, Expr left, Expr right) : Expr
{
    public override double Eval(EvalContext ctx)
    {
        // && / || short-circuit, matching C-like evaluation.
        if (op == "&&") return left.Eval(ctx) != 0.0 && right.Eval(ctx) != 0.0 ? 1.0 : 0.0;
        if (op == "||") return left.Eval(ctx) != 0.0 || right.Eval(ctx) != 0.0 ? 1.0 : 0.0;

        var a = left.Eval(ctx);
        var b = right.Eval(ctx);
        return op switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => b == 0.0 ? 0.0 : a / b,            // ns-eel returns 0 on divide-by-zero
            "%" => b == 0.0 ? 0.0 : (int)a % (int)b,
            "^" => Math.Pow(a, b),
            "&" => (int)a & (int)b,
            "|" => (int)a | (int)b,
            "==" => a == b ? 1.0 : 0.0,
            "!=" => a != b ? 1.0 : 0.0,
            "<" => a < b ? 1.0 : 0.0,
            ">" => a > b ? 1.0 : 0.0,
            "<=" => a <= b ? 1.0 : 0.0,
            ">=" => a >= b ? 1.0 : 0.0,
            _ => 0.0
        };
    }
}

public sealed class AssignExpr(IAssignable target, char compound, Expr value) : Expr
{
    public override double Eval(EvalContext ctx)
    {
        double result;
        if (compound == '=')
        {
            result = value.Eval(ctx);
        }
        else
        {
            var current = ((Expr)target).Eval(ctx);
            var rhs = value.Eval(ctx);
            result = compound switch
            {
                '+' => current + rhs,
                '-' => current - rhs,
                '*' => current * rhs,
                '/' => rhs == 0.0 ? 0.0 : current / rhs,
                '%' => rhs == 0.0 ? 0.0 : (int)current % (int)rhs,
                _ => rhs
            };
        }
        target.Assign(ctx, result);
        return result;
    }
}

/// <summary>A sequence of statements; evaluates to the value of the last.</summary>
public sealed class BlockExpr(Expr[] statements) : Expr
{
    public override double Eval(EvalContext ctx)
    {
        var result = 0.0;
        foreach (var s in statements) result = s.Eval(ctx);
        return result;
    }
}
