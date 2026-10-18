using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlSelectExpression ParseSelect()
    {
        var start = Current;
        Advance();
        bool distinct = Take("DISTINCT");
        if (!distinct) { Take("ALL"); }
        var kindToken = Current;
        bool value = Take("VALUE") || Take("RAW");
        if (value && Is("FROM")) { Error("OQL0002", "SELECT VALUE requires an expression.", kindToken); }
        List<OqlProjection> projections = [];
        do
        {
            var expression = Failed ? new OqlLiteralExpression(null, Span(kindToken, kindToken)) : ParseExpression();
            if (value && expression is OqlStarExpression) { Error("OQL0002", "SELECT VALUE requires an expression other than star.", Previous); }
            string? projectionAlias = !value && Take("AS") ? Identifier(allowKeyword: true) : null;
            projections.Add(new OqlProjection(expression, projectionAlias));
        } while (!value && !Failed && Take(TokenType.Comma));
        if (Failed) { return EmptyExpression(); }
        Expect("FROM");
        string collection = CollectionName();
        string? alias = null;
        if (Take("AS"))
        {
            alias = Identifier();
        }
        else if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function && !IsReturningClause() && !IsUnnestClause())
        {
            alias = Identifier();
        }

        List<OqlUnnestExpression> unnest = [];
        var variables = new HashSet<string>(StringComparer.Ordinal);
        variables.Add(alias ?? collection);
        while (!Failed && IsUnnestClause())
        {
            var bindingStart = Current;
            bool left = Take("LEFT");
            if (left) { Take("OUTER"); }
            else { Take("INNER"); }
            Expect("UNNEST");
            var expression = ParseExpression();
            var variableToken = Current;
            string? variable = null;
            if (Take("AS")) { variableToken = Current; variable = Identifier(); }
            else if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function && !IsUnnestClause() && !IsReturningClause())
            { variable = Identifier(); }
            else if (expression is OqlPathExpression { Segments.Count: > 0 } path)
            {
                variable = path.Segments[^1].Name;
                int pathEnd = _position - 1;
                while (pathEnd > 0 && _tokens[pathEnd].End > path.Location!.End) { pathEnd--; }
                if (_tokens[pathEnd].Type == TokenType.RightBracket) { pathEnd--; }
                variableToken = _tokens[pathEnd];
            }
            if (!Failed && variable is null) { Error("OQL0002", "UNNEST requires an alias for an expression without a final path name.", variableToken); }
            if (!Failed && !variables.Add(variable!)) { Error("OQL0010", ErrorMessages.BindingNameDuplicate, variableToken); }
            unnest.Add(new OqlUnnestExpression(expression, variable ?? string.Empty, left, Span(bindingStart, Previous)));
        }

        OqlExpression? predicate = Take("WHERE") ? ParseExpression() : null;
        List<OqlExpression> groups = [];
        if (Take("GROUP"))
        {
            Expect("BY");
            do { groups.Add(ParseExpression()); } while (!Failed && Take(TokenType.Comma));
        }
        OqlExpression? having = Take("HAVING") ? ParseExpression() : null;
        List<OqlOrdering> ordering = [];
        if (Take("ORDER"))
        {
            Expect("BY");
            do
            {
                var expression = ParseExpression();
                bool descending = Take("DESC");
                if (!descending)
                {
                    Take("ASC");
                }

                ordering.Add(new OqlOrdering(expression, descending));
            } while (!Failed && Take(TokenType.Comma));
        }
        OqlExpression? limit = null;
        OqlExpression? offset = null;
        while (!Failed && (Is("LIMIT") || Is("OFFSET")))
        {
            var clause = Current;
            bool isLimit = Is("LIMIT");
            if (isLimit ? limit is not null : offset is not null)
            {
                Error("OQL0002", $"Multiple {clause.Text.ToUpperInvariant()} clauses are not allowed.", clause);
                break;
            }
            Advance();
            // Refuse the comma spelling before validating its first count, as PostgreSQL's
            // grammar does (src/backend/parser/gram.y:13586-13593).
            if (isLimit && HasCommaCount())
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause("LIMIT m, n (use LIMIT n OFFSET m)", Profile.Language, Span(clause, clause)));
                break;
            }
            var count = ParseCount(clause.Text.ToUpperInvariant());
            if (isLimit) { limit = count; }
            else { offset = count; }
        }
        return new OqlSelectExpression(collection, alias, projections.AsReadOnly(), predicate,
            groups.AsReadOnly(), having, ordering.AsReadOnly(), distinct, value, limit, offset, Span(start, Previous), unnest.AsReadOnly());
    }

    private bool IsUnnestClause() => IsUnnestClause(_position);

    private bool IsUnnestClause(int index) => IsWord(_tokens[index], "UNNEST") ||
        (IsWord(_tokens[index], "INNER") || IsWord(_tokens[index], "LEFT")) && index + 1 < _tokens.Count &&
        (IsWord(_tokens[index + 1], "UNNEST") || IsWord(_tokens[index], "LEFT") && IsWord(_tokens[index + 1], "OUTER") &&
            index + 2 < _tokens.Count && IsWord(_tokens[index + 2], "UNNEST"));

    private bool HasCommaCount()
    {
        int depth = 0;
        for (int index = _position; index < _tokens.Count; index++)
        {
            var token = _tokens[index];
            if (depth == 0)
            {
                if (token.Type == TokenType.Comma) { return index > _position; }
                if (token.Type is TokenType.Eof or TokenType.Semicolon || IsWord(token, "LIMIT") || IsWord(token, "OFFSET") ||
                    IsWord(token, "ORDER") || IsWord(token, "GROUP") || IsWord(token, "HAVING") ||
                    IsWord(token, "WHERE") || IsWord(token, "FROM")) { return false; }
            }
            if (token.Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace) { depth++; }
            else if (token.Type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace)
            {
                if (depth == 0) { return false; }
                depth--;
            }
        }
        return false;
    }

    private OqlExpression ParseCount(string clause)
    {
        var start = Current;
        if (Current.Type is TokenType.Eof or TokenType.Semicolon || Is("LIMIT") || Is("OFFSET"))
        {
            Error("OQL0002", $"Expected an integer literal or parameter after {clause}.", Current);
            return new OqlLiteralExpression(null, Span(start, start));
        }
        // Reuse primary parsing for the shared parameter forms and exact operand spans.
        bool integer = Current.Type == TokenType.Integer || Current.Type is TokenType.Plus or TokenType.Minus &&
            _tokens[_position + 1].Type == TokenType.Integer;
        bool parameter = Current.Type == TokenType.Parameter;
        bool all = Take("ALL");
        var count = all ? new OqlLiteralExpression(null, Span(start, start)) : ParsePrimary();
        if (Failed) { return count; }
        if (!parameter && (!integer || !long.TryParse((start.Type is TokenType.Plus or TokenType.Minus ? start.Text : string.Empty) + Previous.Text,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out long number) || number < 0))
        {
            Error("OQL0007", string.Format(CultureInfo.CurrentCulture, ErrorMessages.PagingCountInvalid, clause),
                new Lexeme(start.Type, _source[start.Start..Previous.End], start.Start, Previous.End, start.Line));
        }
        return count;
    }

    private bool IsReturningClause()
    {
        if (!Is("RETURNING")) { return false; }
        var next = _tokens[_position + 1];
        // An implicit source alias must be followed by the end or another clause. Keep
        // RETURNING as that name there; otherwise leave the trailing clause for rejection.
        return next.Type is not (TokenType.Eof or TokenType.Semicolon) &&
            !IsWord(next, "WHERE") && !IsWord(next, "GROUP") && !IsWord(next, "HAVING") &&
            !IsWord(next, "ORDER") && !IsWord(next, "LIMIT") && !IsWord(next, "OFFSET") && !(IsWord(next, "RETURNING") &&
                _tokens[_position + 2].Type is not (TokenType.Eof or TokenType.Semicolon));
    }
}
