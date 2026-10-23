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
                Error("CG00002", ErrorMessages.GqlQueryParserParseExpressionVoid, Current);
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
                    else if (!Take("UNKNOWN")) { Error("CG00002", ErrorMessages.GqlQueryParserParseExpressionVoid2, Current); }
                    left = new GqlTruthTestExpression(left, truth, negated, Span(start, Previous));
                }
            }
            else if (op == "IN")
            {
                if (Current.Type == TokenType.LeftParen && StartsTuple())
                { Error("CG00002", ErrorMessages.GqlQueryParserParseExpressionVoid3, Current); break; }
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
        if (Failed) { return new GqlLiteralExpression(null, Span(start, start)); }
        if (Is("CASE")) { return ParseCase(); }
        if (Is("COALESCE") || Is("NULLIF")) { return ParseCaseAbbreviation(); }
        if (Current.Type == TokenType.Function && Is("CAST") && _tokens[_position + 1].Type == TokenType.LeftParen) { return ParseCast(); }
        if (Current.Type == TokenType.Function && (Is("CURRENT_DATE") || Is("CURRENT_TIMESTAMP")))
        {
            Advance();
            return new GqlFunctionCallExpression(start.Text.Equals("CURRENT_DATE", StringComparison.OrdinalIgnoreCase) ? GqlScalarFunction.CurrentDate : GqlScalarFunction.CurrentTimestamp, [], Span(start, Previous));
        }
        if (IsReservedTemporal(Current))
        {
            Unsupported(Current.Text.ToUpperInvariant(), Current);
            return new GqlLiteralExpression(null, Span(start, start));
        }
        if (Current.Type == TokenType.Function && _tokens[_position + 1].Type == TokenType.LeftParen)
        {
            if (start.Text.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" or "COLLECT_LIST" or "COLLECT")
            { return ParseAggregate(); }
            return ParseFunction();
        }
        if (Current.Type == TokenType.LeftBracket) { return ParseList(); }
        if (Current.Type == TokenType.LeftBrace) { return ParseMap(); }
        if (IsVariableToken)
        {
            string variable = Identifier();
            if (!Take(TokenType.Dot)) { return new GqlVariableExpression(variable, Span(start, Previous)); }
            string property = Identifier(allowKeyword: true);
            if (!Failed && Current.Type == TokenType.Dot) { Unsupported("NESTED PROPERTY ACCESS", Current); }
            return new GqlPropertyExpression(variable, property, Span(start, Previous));
        }
        return ParseScalar();
    }

    private GqlExpression ParseAggregate()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
        Advance();
        Expect(TokenType.LeftParen, "'('");
        var function = start.Text.ToUpperInvariant() switch
        {
            "COUNT" => GqlAggregateFunction.Count,
            "SUM" => GqlAggregateFunction.Sum,
            "AVG" => GqlAggregateFunction.Avg,
            "MIN" => GqlAggregateFunction.Min,
            "MAX" => GqlAggregateFunction.Max,
            _ => GqlAggregateFunction.CollectList,
        };
        bool distinct = Take("DISTINCT");
        bool all = !distinct && Take("ALL");
        GqlExpression? operand = null;
        if (Take(TokenType.Asterisk))
        {
            if (function != GqlAggregateFunction.Count || distinct || all)
            { Error("CG00002", ErrorMessages.GqlQueryParserParseAggregateVoid, Previous); }
        }
        else { operand = ParsePredicate(); }
        Expect(TokenType.RightParen, "')'");
        return new GqlAggregateExpression(function, operand, distinct, Span(start, Previous));
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
        { Error("CG00002", ErrorMessages.GqlQueryParserParseScalarVoid, parameter); }
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
            _diagnostics.Add(QueryDiagnostics.UnsupportedParameterForm("CG00002", Span(start, end)));
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
                else { Error("CG00004", ErrorMessages.GqlQueryParserParseLiteralVoid, start); }
            }
            else if (Take(TokenType.Float))
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating) && double.IsFinite(floating))
                {
                    value = floating;
                }
                else { Error("CG00004", ErrorMessages.GqlQueryParserParseLiteralVoid2, start); }
            }
            else if (Current.Type is TokenType.LeftBracket or TokenType.LeftBrace) { Error("CK00001", Current.Type == TokenType.LeftBracket ? ErrorMessages.GqlQueryParserParseLiteralVoid3 : ErrorMessages.GqlQueryParserParseLiteralVoid4, Current); }
            else { Error("CG00002", ErrorMessages.GqlQueryParserParseLiteralVoid5, start); }
        }
        return new GqlLiteralExpression(value, Span(start, Previous));
    }
}
