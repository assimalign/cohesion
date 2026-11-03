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
        var query = ParseQueryCore();
        if (!Failed && query is OqlSetOperationExpression)
        {
            var pending = new Stack<(OqlQueryExpression Query, int Depth)>();
            pending.Push((query, 1));
            while (pending.TryPop(out var item))
            {
                if (item.Depth > 128)
                { Error("CD00005", ErrorMessages.OqlQueryParserParseExpressionVoid, start); break; }
                if (item.Query is OqlSetOperationExpression compound)
                {
                    pending.Push((compound.Left, item.Depth + 1));
                    pending.Push((compound.Right, item.Depth + 1));
                }
            }
        }
        return query;
    }

    private OqlQueryExpression ParseQueryCore()
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
            query = new OqlSetOperationExpression(query, right, operation, all, location: Span(start, Previous));
        }
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
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("outer ordering or paging on a lone parenthesized query", Profile.Language, Span(start, Previous)));
            return query;
        }
        if (query is OqlSetOperationExpression compound)
        {
            return new OqlSetOperationExpression(compound.Left, compound.Right, compound.Operation,
                compound.IsAll, ordering.AsReadOnly(), limit, offset, Span(start, Previous));
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
        if (query is OqlSetOperationExpression)
        { _diagnostics.Add(QueryDiagnostics.UnsupportedClause("compound query in a nested query position", Profile.Language, query.Location!)); }
    }

    private bool IsParenthesizedQuery()
    {
        int index = _position;
        if (_tokens[index].Type != TokenType.LeftParen) { return false; }
        while (_tokens[index].Type == TokenType.LeftParen) { index++; }
        return IsWord(_tokens[index], "SELECT");
    }

    private bool IsSetOperator() => Is("UNION") || Is("INTERSECT") || Is("EXCEPT");
}
