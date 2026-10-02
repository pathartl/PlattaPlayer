using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlattaPlayer.Visualizations.MilkDrop.Expressions;

internal enum TokenKind { Number, Ident, Op, LParen, RParen, LBracket, RBracket, Comma, Semicolon, End }

internal readonly struct Token(TokenKind kind, string text, double number)
{
    public readonly TokenKind Kind = kind;
    public readonly string Text = text;
    public readonly double Number = number;
}

/// <summary>
/// Tokenises MilkDrop/ns-eel equation source. Identifiers are lowercased to match MilkDrop's
/// case-insensitive compiler. Supports <c>//</c> and <c>/* */</c> comments and the <c>$pi</c>/<c>$e</c>/
/// <c>$phi</c> / <c>$xHEX</c> constant forms.
/// </summary>
internal static class Lexer
{
    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var i = 0;
        var n = source.Length;

        while (i < n)
        {
            var c = source[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Comments.
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                i += 2;
                while (i < n && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i += 2;
                continue;
            }

            // Constants: $pi, $e, $phi, $xHEX.
            if (c == '$')
            {
                var start = ++i;
                while (i < n && (char.IsLetterOrDigit(source[i]))) i++;
                var word = source.Substring(start, i - start).ToLowerInvariant();
                var value = word switch
                {
                    "pi" => Math.PI,
                    "e" => Math.E,
                    "phi" => 1.618033988749895,
                    _ when word.StartsWith('x') && long.TryParse(word.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) => hex,
                    _ => 0.0
                };
                tokens.Add(new Token(TokenKind.Number, word, value));
                continue;
            }

            // Numbers (incl. leading-dot floats and scientific notation).
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(source[i + 1])))
            {
                var start = i;
                while (i < n && (char.IsDigit(source[i]) || source[i] == '.')) i++;
                if (i < n && (source[i] == 'e' || source[i] == 'E'))
                {
                    i++;
                    if (i < n && (source[i] == '+' || source[i] == '-')) i++;
                    while (i < n && char.IsDigit(source[i])) i++;
                }
                var text = source.Substring(start, i - start);
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
                tokens.Add(new Token(TokenKind.Number, text, value));
                continue;
            }

            // Identifiers.
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Ident, source.Substring(start, i - start).ToLowerInvariant(), 0));
                continue;
            }

            switch (c)
            {
                case '(': tokens.Add(new Token(TokenKind.LParen, "(", 0)); i++; continue;
                case ')': tokens.Add(new Token(TokenKind.RParen, ")", 0)); i++; continue;
                case '[': tokens.Add(new Token(TokenKind.LBracket, "[", 0)); i++; continue;
                case ']': tokens.Add(new Token(TokenKind.RBracket, "]", 0)); i++; continue;
                case ',': tokens.Add(new Token(TokenKind.Comma, ",", 0)); i++; continue;
                case ';': tokens.Add(new Token(TokenKind.Semicolon, ";", 0)); i++; continue;
            }

            // Operators, longest match first.
            var two = i + 1 < n ? source.Substring(i, 2) : string.Empty;
            if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "+=" or "-=" or "*=" or "/=" or "%=")
            {
                tokens.Add(new Token(TokenKind.Op, two, 0));
                i += 2;
                continue;
            }

            if ("+-*/%^&|<>=!".IndexOf(c) >= 0)
            {
                tokens.Add(new Token(TokenKind.Op, c.ToString(), 0));
                i++;
                continue;
            }

            // Unknown character — skip it rather than failing the whole preset.
            i++;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, 0));
        return tokens;
    }
}
