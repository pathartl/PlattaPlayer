using System;
using System.Collections.Generic;

namespace PlattaPlayer.Visualizations.MilkDrop.Expressions;

/// <summary>
/// Recursive-descent parser turning tokenised equation source into an <see cref="Expr"/> tree.
/// Identifiers resolve to slots in a shared <see cref="VariableTable"/> at parse time. The parser is
/// deliberately forgiving: malformed presets should degrade (skip the bad bit) rather than crash the
/// visualizer.
/// </summary>
internal sealed class Parser
{
    private readonly List<Token> _tokens;
    private readonly VariableTable _vars;
    private int _pos;

    private Parser(List<Token> tokens, VariableTable vars)
    {
        _tokens = tokens;
        _vars = vars;
    }

    public static Expr Parse(string source, VariableTable vars)
    {
        var parser = new Parser(Lexer.Tokenize(source), vars);
        return parser.ParseProgram();
    }

    private Token Current => _tokens[_pos];
    private Token Advance() => _tokens[_pos++];
    private bool IsOp(string op) => Current.Kind == TokenKind.Op && Current.Text == op;

    private Expr ParseProgram()
    {
        var statements = new List<Expr>();
        while (Current.Kind != TokenKind.End)
        {
            if (Current.Kind == TokenKind.Semicolon) { _pos++; continue; }
            statements.Add(ParseAssignment());
            if (Current.Kind == TokenKind.Semicolon) _pos++;
            else if (Current.Kind != TokenKind.End) _pos++; // resync on stray tokens
        }
        return new BlockExpr(statements.ToArray());
    }

    private Expr ParseAssignment()
    {
        var left = ParseBinary(1);
        if (Current.Kind == TokenKind.Op && IsAssignOp(Current.Text) && left is IAssignable target)
        {
            var op = Advance().Text;
            var right = ParseAssignment();
            return new AssignExpr(target, op[0], right);
        }
        return left;
    }

    private static bool IsAssignOp(string op)
        => op is "=" or "+=" or "-=" or "*=" or "/=" or "%=";

    private Expr ParseBinary(int minPrec)
    {
        var left = ParseUnary();
        while (Current.Kind == TokenKind.Op && !IsAssignOp(Current.Text))
        {
            var op = Current.Text;
            var prec = Precedence(op);
            if (prec < minPrec) break;

            Advance();
            var rightAssoc = op == "^";
            var right = ParseBinary(rightAssoc ? prec : prec + 1);
            left = new BinaryExpr(op, left, right);
        }
        return left;
    }

    private static int Precedence(string op) => op switch
    {
        "||" => 1,
        "&&" => 2,
        "|" => 3,
        "&" => 4,
        "==" or "!=" => 5,
        "<" or ">" or "<=" or ">=" => 6,
        "+" or "-" => 7,
        "*" or "/" or "%" => 8,
        "^" => 9,
        _ => -1
    };

    private Expr ParseUnary()
    {
        if (IsOp("-")) { Advance(); return new UnaryExpr('-', ParseUnary()); }
        if (IsOp("!")) { Advance(); return new UnaryExpr('!', ParseUnary()); }
        if (IsOp("+")) { Advance(); return ParseUnary(); }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var e = ParsePrimary();
        while (Current.Kind == TokenKind.LBracket)
        {
            Advance();
            var index = ParseAssignment();
            if (Current.Kind == TokenKind.RBracket) Advance();
            e = new IndexExpr(e, index);
        }
        return e;
    }

    private Expr ParsePrimary()
    {
        var tok = Current;

        if (tok.Kind == TokenKind.Number) { Advance(); return new NumberExpr(tok.Number); }

        if (tok.Kind == TokenKind.LParen)
        {
            Advance();
            var inner = ParseAssignment();
            if (Current.Kind == TokenKind.RParen) Advance();
            return inner;
        }

        if (tok.Kind == TokenKind.Ident)
        {
            Advance();
            var name = tok.Text;

            if (Current.Kind == TokenKind.LParen)
            {
                var args = ParseArgList();
                if (name == "megabuf") return new MemoryExpr(Arg(args, 0), global: false);
                if (name == "gmegabuf") return new MemoryExpr(Arg(args, 0), global: true);

                var func = CallExpr.Lookup(name);
                if (func is { } f) return new CallExpr(f, args);

                // Unknown function: keep parsing valid, yield 0.
                return new NumberExpr(0);
            }

            return new VarExpr(_vars.Intern(name));
        }

        // Unexpected token: consume and yield 0 so a single bad token can't wedge the parser.
        Advance();
        return new NumberExpr(0);
    }

    private Expr[] ParseArgList()
    {
        Advance(); // consume '('
        var args = new List<Expr>();
        if (Current.Kind != TokenKind.RParen)
        {
            args.Add(ParseAssignment());
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                args.Add(ParseAssignment());
            }
        }
        if (Current.Kind == TokenKind.RParen) Advance();
        return args.ToArray();
    }

    private static Expr Arg(Expr[] args, int i) => i < args.Length ? args[i] : new NumberExpr(0);
}
