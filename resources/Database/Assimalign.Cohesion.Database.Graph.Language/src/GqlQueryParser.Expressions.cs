using System;
using System.Collections.Generic;
using System.Globalization;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    // predicate := comparison ('AND' comparison)*
    // One n-ary node holds the whole chain, so no count of comparisons applies; only parentheses
    // nest, and they are bounded by the stack (GQL0009).
    private GqlExpression ParsePredicate()
    {
        var start = Current;
        var first = ParseComparison();
        if (Failed || !Is("AND")) { return first; }

        // A parenthesized chain that opens this one merges into it: (p AND q) AND r is p AND q AND r.
        // ParseComparison returns a logical node only from inside parentheses.
        List<GqlExpression> operands = first is GqlLogicalExpression { Operator: GqlLogicalOperator.And } opening
            ? opening.DetachOperands()
            : [first];
        while (!Failed && Take("AND"))
        {
            operands.Add(ParseComparison());
        }
        return GqlLogicalExpression.FromOwnedList(GqlLogicalOperator.And, operands, Span(start, Previous));
    }

    private GqlExpression ParseComparison()
    {
        var start = Current;
        if (Current.Type == TokenType.LeftParen)
        {
            if (!HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
            Advance();
            var predicate = ParsePredicate();
            Expect(TokenType.RightParen, "')'");
            return predicate;
        }
        // n:A and n IS [NOT] LABELED A are Boolean primaries, not comparison operands.
        if (StartsLabeledPredicate()) { return ParseLabeledPredicate(); }
        var left = ParseOperand();
        string op = Current.Text;
        if (Current.Type is not (TokenType.Equals or TokenType.NotEquals or TokenType.LessThan or
            TokenType.LessEqual or TokenType.GreaterThan or TokenType.GreaterEqual))
        {
            Error("GQL0002", "Expected a scalar comparison operator.", Current);
            return left;
        }
        Advance();
        return new GqlBinaryExpression(left, op == "<>" ? "!=" : op, ParseOperand(), Span(start, Previous));
    }

    private GqlExpression ParseOperand()
    {
        var start = Current;
        if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier)
        {
            string variable = Identifier();
            Expect(TokenType.Dot, "'.'");
            string property = Identifier(allowKeyword: true);
            return new GqlPropertyExpression(variable, property, Span(start, Previous));
        }
        return ParseLiteral();
    }

    private GqlLiteralExpression ParseLiteral()
    {
        var start = Current;
        object? value = null;
        if (Take("NULL")) { }
        else if (Take("TRUE")) { value = true; }
        else if (Take("FALSE")) { value = false; }
        else if (Take(TokenType.String)) { value = start.Text[1..^1].Replace("''", "'", StringComparison.Ordinal); }
        else
        {
            bool negative = Take(TokenType.Minus);
            if (!negative) { Take(TokenType.Plus); }
            var number = Current;
            string text = negative ? "-" + number.Text : number.Text;
            if (Take(TokenType.Integer))
            {
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer)) { value = integer; }
                else { Error("GQL0004", "Integer literal is outside the signed 64-bit range.", start); }
            }
            else if (Take(TokenType.Float))
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating) && double.IsFinite(floating))
                {
                    value = floating;
                }
                else { Error("GQL0004", "Floating-point literal is invalid or outside the finite double range.", start); }
            }
            else if (Current.Type is TokenType.LeftBracket or TokenType.LeftBrace) { Unsupported("COLLECTION LITERAL", Current); }
            else { Error("GQL0002", "Expected a scalar literal.", start); }
        }
        return new GqlLiteralExpression(value, Span(start, Previous));
    }
}
