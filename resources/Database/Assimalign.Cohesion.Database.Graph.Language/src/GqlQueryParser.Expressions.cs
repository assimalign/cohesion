using System;
using System.Collections.Generic;
using System.Globalization;
using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlExpression ParsePredicate() => ParseExpression(0);

    // A run is iterative. Right operands descend through the finite precedence levels;
    // unary operands, groups and nested lists check the stack before unbounded descent.
    private GqlExpression ParseExpression(int minimum)
    {
        var start = Current;
        bool recursive = Is("NOT") || Current.Type == TokenType.LeftParen ||
            Current.Type is TokenType.Plus or TokenType.Minus && _tokens[_position + 1].Type is not (TokenType.Integer or TokenType.Float);
        if (recursive && !HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
        GqlExpression left;
        if (Take("NOT"))
        {
            left = new GqlUnaryExpression("NOT", ParseExpression(4), Span(start, Previous));
        }
        else if (Current.Type is TokenType.Plus or TokenType.Minus &&
                 _tokens[_position + 1].Type is not (TokenType.Integer or TokenType.Float))
        {
            Advance();
            left = new GqlUnaryExpression(start.Text, ParseExpression(9), Span(start, Previous));
        }
        else if (Take(TokenType.LeftParen))
        {
            left = ParseExpression(0);
            Expect(TokenType.RightParen, "')'");
        }
        else if (StartsLabeledPredicate()) { left = ParseLabeledPredicate(); }
        else { left = ParseOperand(); }

        left = ParsePostfix(left, start);
        bool compared = false;
        while (!Failed)
        {
            string op = Current.Text.ToUpperInvariant();
            int precedence = op switch
            {
                "OR" or "XOR" => 1, "AND" => 3,
                "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "<-" or
                    "IN" or "STARTS" or "ENDS" or "CONTAINS" or "IS" => 5,
                "||" => 6, "+" or "-" => 7, "*" or "/" or "%" => 8,
                _ => -1,
            };
            if (precedence < minimum) { break; }
            if (precedence == 5 && compared && op != "IS")
            {
                Error("GQL0002", "Comparison predicates cannot be chained; use AND or parentheses.", Current);
                break;
            }
            if (op == "<-")
            {
                // The shared pattern lexer emits one arrow. A scalar expression reads it as < -.
                var arrow = Current;
                _tokens[_position] = arrow with { Type = TokenType.Minus, Text = "-", Start = arrow.Start + 1 };
                op = "<";
            }
            else { Advance(); }
            if (op == "IS")
            {
                bool negated = Take("NOT");
                if (Take("NULL")) { left = new GqlIsNullExpression(left, negated, Span(start, Previous)); }
                else
                {
                    bool? truth = null;
                    if (Take("TRUE")) { truth = true; }
                    else if (Take("FALSE")) { truth = false; }
                    else if (!Take("UNKNOWN")) { Error("GQL0002", "Expected NULL, TRUE, FALSE or UNKNOWN after IS [NOT].", Current); }
                    left = new GqlTruthTestExpression(left, truth, negated, Span(start, Previous));
                }
            }
            else if (op == "IN")
            {
                if (Current.Type == TokenType.LeftParen && StartsTuple())
                { Error("GQL0002", "IN requires a list value, such as the [...] list form.", Current); break; }
                var list = ParseExpression(precedence + 1);
                left = new GqlInExpression(left, list, Span(start, Previous));
            }
            else
            {
                if (op is "STARTS" or "ENDS") { Expect("WITH"); }
                var right = ParseExpression(precedence + 1);
                if (op is "AND" or "OR" or "XOR")
                {
                    var logical = op == "AND" ? GqlLogicalOperator.And : op == "OR" ? GqlLogicalOperator.Or : GqlLogicalOperator.Xor;
                    List<GqlExpression> operands = left is GqlLogicalExpression opening && opening.Operator == logical
                        ? opening.DetachOperands() : [left];
                    operands.Add(right);
                    left = GqlLogicalExpression.FromOwnedList(logical, operands, Span(start, Previous));
                }
                else if (op is "STARTS" or "ENDS" or "CONTAINS")
                {
                    var predicate = op == "STARTS" ? GqlStringPredicateOperator.StartsWith :
                        op == "ENDS" ? GqlStringPredicateOperator.EndsWith : GqlStringPredicateOperator.Contains;
                    left = new GqlStringPredicateExpression(left, predicate, right, Span(start, Previous));
                }
                else { left = new GqlBinaryExpression(left, op == "<>" ? "!=" : op, right, Span(start, Previous)); }
            }
            compared |= precedence == 5;
        }
        return left;
    }

    private GqlExpression ParseOperand()
    {
        var start = Current;
        if (Is("CASE")) { return ParseCase(); }
        if (Is("COALESCE") || Is("NULLIF")) { return ParseCaseAbbreviation(); }
        if (Current.Type == TokenType.LeftBracket) { return ParseList(); }
        if (Current.Type == TokenType.LeftBrace) { return ParseMap(); }
        if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier)
        {
            string variable = Identifier();
            if (!Take(TokenType.Dot)) { return new GqlVariableExpression(variable, Span(start, Previous)); }
            string property = Identifier(allowKeyword: true);
            if (!Failed && Current.Type == TokenType.Dot) { Unsupported("NESTED PROPERTY ACCESS", Current); }
            return new GqlPropertyExpression(variable, property, Span(start, Previous));
        }
        return ParseScalar();
    }

    private void RejectUnsupportedParameter(string form)
    {
        var parameter = Current;
        _ = ParseScalar();
        if (!Failed) { Unsupported(form, parameter); }
    }

    private GqlExpression ParseScalar()
    {
        if (Current.Type != TokenType.Parameter) { return ParseLiteral(); }
        var parameter = Current;
        Advance();
        if (!ParameterBindings.IsValidReference(parameter.Text))
        { Error("GQL0002", "Expected a named parameter after $ or @.", parameter); }
        return new GqlParameterExpression(ParameterBindings.BareName(parameter.Text), Span(parameter, parameter));
    }

    private GqlLiteralExpression ParseLiteral()
    {
        var start = Current;
        if (Current.Type == TokenType.Colon || Current.Text == "?")
        {
            Advance();
            var end = start;
            if (start.Type == TokenType.Colon && Current.Start == start.End && Current.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function)
            {
                end = Current;
                Advance();
            }
            _parameterFormPositions.Add(start.Start);
            _diagnostics.Add(QueryDiagnostics.UnsupportedParameterForm("GQL0002", Span(start, end)));
            return new GqlLiteralExpression(null, Span(start, end));
        }
        object? value = null;
        if (Take("NULL") || Take("UNKNOWN")) { }
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
            else if (Current.Type is TokenType.LeftBracket or TokenType.LeftBrace) { Error("COHDBL001", Current.Type == TokenType.LeftBracket ? "List-valued properties are unsupported by graph storage." : "Map-valued properties are unsupported by graph storage.", Current); }
            else { Error("GQL0002", "Expected a scalar literal.", start); }
        }
        return new GqlLiteralExpression(value, Span(start, Previous));
    }
}
