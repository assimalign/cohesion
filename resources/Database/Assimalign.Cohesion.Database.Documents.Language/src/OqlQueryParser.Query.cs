using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    // Couchbase select ::= select-term (set-op select-term)* order-by? limit? offset?.
    // Each operator has equal precedence; the loop builds the left-associated tree.
    private OqlQueryExpression ParseQuery()
    {
        var start = Current;
        var query = ParseQueryTerm();
        List<(string Name, Lexeme Start, Lexeme End)> deferred = [];
        int operations = 0;
        while (!Failed && IsSetOperator())
        {
            if (++operations > 128)
            { Error("CD00005", ErrorMessages.OqlQueryParserParseExpressionVoid, Current); break; }
            var operatorToken = Current;
            var operation = Current.Text.ToUpperInvariant() switch
            {
                "INTERSECT" => OqlSetOperation.Intersect,
                "EXCEPT" => OqlSetOperation.Except,
                _ => OqlSetOperation.Union,
            };
            Advance();
            bool all = Take("ALL");
            if (operation != OqlSetOperation.Union)
            { deferred.Add((operatorToken.Text.ToUpperInvariant(), operatorToken, Previous)); }
            // The final grammar admits only ALL; explicit DISTINCT is not SQL++ syntax.
            var right = ParseQueryTerm();
            // Leaves have zero operators. Include parenthesized subtrees without counting
            // their leaf as another level, and locate the operator that exceeds the bound.
            int depth = 1 + System.Math.Max(_queryOperationDepths.GetValueOrDefault(query), _queryOperationDepths.GetValueOrDefault(right));
            if (!Failed && depth > 128)
            { Error("CD00005", ErrorMessages.OqlQueryParserParseExpressionVoid, operatorToken); break; }
            query = new OqlSetOperationExpression(query, right, operation, all, location: Span(start, Previous));
            _queryOperationDepths[query] = depth;
        }
        var tailStart = Current;
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

                var placement = OqlNullPlacement.Default;
                if (Take("NULLS"))
                {
                    if (Take("FIRST")) { placement = OqlNullPlacement.First; }
                    else if (Take("LAST")) { placement = OqlNullPlacement.Last; }
                    else { Error("CD00013", ErrorMessages.NullPlacementExpected, Current); }
                }
                ordering.Add(new OqlOrdering(expression, descending, placement));
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
                Error("CD00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserParseSelectVoid4, clause.Text.ToUpperInvariant()), clause);
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
        foreach (var refused in deferred)
        { _diagnostics.Add(QueryDiagnostics.UnsupportedClause(refused.Name, Profile.Language, Span(refused.Start, refused.End))); }
        if (ordering.Count == 0 && limit is null && offset is null) { return query; }
        if (query.OrderBy.Count > 0 || query.Limit is not null || query.Offset is not null)
        {
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("outer ordering or paging on a lone parenthesized query", Profile.Language, Span(tailStart, Previous)));
            return query;
        }
        if (query is OqlSetOperationExpression compound)
        {
            var result = new OqlSetOperationExpression(compound.Left, compound.Right, compound.Operation,
                compound.IsAll, ordering.AsReadOnly(), limit, offset, Span(start, Previous));
            _queryOperationDepths[result] = _queryOperationDepths[compound];
            return result;
        }
        var select = (OqlSelectExpression)query;
        return new OqlSelectExpression(select.Collection, select.Alias, select.Projections, select.Predicate,
            select.GroupBy, select.Having, ordering.AsReadOnly(), select.IsDistinct, select.IsValue, limit, offset,
            Span(start, Previous), select.Unnest, source: select.Source,
            bindings: select.Bindings == select.Unnest ? null : select.Bindings, isAliasImplicit: select.IsAliasImplicit);
    }

    private OqlQueryExpression ParseQueryTerm()
    {
        if (Is("SELECT")) { return ParseSelect(); }
        var start = Current;
        if (Take(TokenType.LeftParen))
        {
            if (++_depth > 128)
            {
                Error("CD00005", ErrorMessages.OqlQueryParserParseExpressionVoid, start);
                _depth--;
                return EmptyExpression();
            }
            var query = ParseQuery();
            _depth--;
            if (!Failed) { Expect(TokenType.RightParen, "')'"); }
            return query;
        }
        Expect("SELECT");
        return EmptyExpression();
    }

    private void RefuseNestedCompound(OqlQueryExpression query)
    {
        if (!Failed && query is OqlSetOperationExpression)
        { _diagnostics.Add(QueryDiagnostics.UnsupportedClause("compound query in a nested query position", Profile.Language, query.Location!)); }
    }

    private bool IsParenthesizedQuery(bool expressionOperand = false)
    {
        int index = _position;
        if (_tokens[index].Type != TokenType.LeftParen) { return false; }
        int wrappers = 0;
        while (_tokens[index].Type == TokenType.LeftParen) { index++; wrappers++; }
        if (!IsWord(_tokens[index], "SELECT")) { return false; }
        if (!expressionOperand) { return true; }
        // A parenthesized subquery can also begin a larger scalar expression or IN list.
        // Check each leading wrapper at its matching close, innermost first, as SQL does
        // (SqlQueryParser.SetOperations.cs; PostgreSQL gram.y:15645,15899,15916).
        int nesting = wrappers;
        int remaining = wrappers;
        for (; index < _tokens.Count - 1; index++)
        {
            if (_tokens[index].Type == TokenType.LeftParen) { nesting++; }
            else if (_tokens[index].Type == TokenType.RightParen && --nesting < remaining)
            {
                var next = _tokens[index + 1];
                bool continuation = next.Type is TokenType.RightParen or TokenType.Eof ||
                    IsWord(next, "UNION") || IsWord(next, "INTERSECT") || IsWord(next, "EXCEPT") ||
                    IsWord(next, "ORDER") || IsWord(next, "LIMIT") || IsWord(next, "OFFSET");
                if (!continuation) { return false; }
                if (--remaining == 0) { return true; }
            }
        }
        return true;
    }

    private bool IsSetOperator() => Is("UNION") || Is("INTERSECT") || Is("EXCEPT");
}
