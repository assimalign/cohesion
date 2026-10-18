using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlQueryExpression ParseQuery()
    {
        var start = Current;
        List<GqlMatchClause> matches = [];
        IReadOnlyList<GqlPathPattern> creates = [];
        List<string> deletes = [];
        List<GqlProjection> projections = [];
        bool detach = false;
        bool returnStar = false;
        List<GqlSortItem> orderBy = [];
        GqlExpression? offset = null;
        GqlExpression? limit = null;
        var quantifier = GqlSetQuantifier.All;
        while (!Failed && (Is("MATCH") || Is("OPTIONAL")))
        {
            bool optional = Take("OPTIONAL");
            Expect("MATCH");
            var paths = Failed ? [] : ParsePatterns(allowPathVariable: true);
            GqlExpression? predicate = null;
            if (!Failed && Take("WHERE")) { predicate = ParsePredicate(); }
            matches.Add(new GqlMatchClause(paths, predicate, optional));
        }
        if (!Failed && (Is("ORDER") || Is("OFFSET") || Is("SKIP") || Is("LIMIT")))
        {
            Unsupported("STANDALONE ORDER BY AND PAGE STATEMENT", Current);
        }
        if (!Failed && (Take("CREATE") || Take("INSERT"))) { creates = ParsePatterns(); }
        else if (!Failed && (Is("DETACH") || Is("DELETE")))
        {
            detach = Take("DETACH");
            Expect("DELETE");
            do { deletes.Add(Identifier()); } while (!Failed && Take(TokenType.Comma));
        }
        if (!Failed && (Is("ORDER") || Is("OFFSET") || Is("SKIP") || Is("LIMIT")))
        {
            Unsupported("STANDALONE ORDER BY AND PAGE STATEMENT", Current);
        }
        if (!Failed && Take("RETURN"))
        {
            if (Take("DISTINCT")) { quantifier = GqlSetQuantifier.Distinct; }
            else { Take("ALL"); }
            returnStar = Take(TokenType.Asterisk);
            if (returnStar && !matches.Exists(clause => HasNamedBinding(clause.Paths)) && !HasNamedBinding(creates))
            { Error("GQL0002", "RETURN * requires at least one named binding.", Previous); }
            if (!returnStar)
            {
                do
                {
                    int first = _position;
                    var expression = ParsePredicate();
                    expression.SetStatementText(NormalizeProjectionText(first, _position));
                    if (!Failed && Current.Type == TokenType.LeftBrace) { Unsupported("MAP PROJECTION", Current); }
                    string? alias = Take("AS") ? Identifier() : null;
                    projections.Add(new GqlProjection(expression, alias));
                } while (!Failed && Take(TokenType.Comma));
            }
            if (!Failed && Take("ORDER"))
            {
                Expect("BY");
                do
                {
                    var expression = ParsePredicate();
                    var direction = Take("DESC") || Take("DESCENDING") ? GqlSortDirection.Descending : GqlSortDirection.Ascending;
                    if (direction == GqlSortDirection.Ascending) { if (!Take("ASC")) { Take("ASCENDING"); } }
                    var nullOrdering = GqlNullOrdering.Default;
                    if (Take("NULLS"))
                    {
                        if (Take("FIRST")) { nullOrdering = GqlNullOrdering.First; }
                        else if (Take("LAST")) { nullOrdering = GqlNullOrdering.Last; }
                        else { Error("GQL0002", "Expected FIRST or LAST after NULLS.", Current); }
                    }
                    orderBy.Add(new GqlSortItem(expression, direction, nullOrdering));
                } while (!Failed && Take(TokenType.Comma));
            }
            while (!Failed && (Is("OFFSET") || Is("SKIP") || Is("LIMIT")))
            {
                bool limiting = Is("LIMIT");
                if (limit is not null || !limiting && offset is not null)
                { Error("GQL0002", "Expected OFFSET or SKIP before LIMIT, with each paging clause at most once.", Current); break; }
                Advance();
                var value = ParsePageValue();
                if (limiting) { limit = value; } else { offset = value; }
            }
        }
        if (!Failed && creates.Count == 0 && deletes.Count == 0 && projections.Count == 0 && !returnStar)
        {
            Error("GQL0002", "MATCH requires RETURN, INSERT, CREATE, or DELETE.", Current);
        }
        if (!Failed && deletes.Count != 0 && (projections.Count != 0 || returnStar))
        {
            Unsupported("RETURN AFTER DELETE", Previous);
        }
        return new GqlQueryExpression(matches.AsReadOnly(), creates, deletes.AsReadOnly(), detach,
            projections.AsReadOnly(), Span(start, Previous))
        { SetQuantifier = quantifier, ReturnStar = returnStar, OrderBy = orderBy.AsReadOnly(), Offset = offset, Limit = limit };
    }

    private GqlExpression ParsePageValue()
    {
        if (Current.Type is TokenType.Integer or TokenType.Parameter or TokenType.Colon || Current.Text == "?") { return ParseScalar(); }
        Error("GQL0002", "Expected an unsigned integer or parameter.", Current);
        return new GqlLiteralExpression(null, Span(Current, Current));
    }

    private static bool HasNamedBinding(IReadOnlyList<GqlPathPattern> paths)
    {
        foreach (var path in paths)
        {
            if (path.Variable is not null) { return true; }
            foreach (var node in path.Nodes) { if (node.Variable is not null) { return true; } }
            foreach (var edge in path.Relationships) { if (edge.Variable is not null) { return true; } }
        }
        return false;
    }

    // Normalize token spacing, preserving spelling, quoted text and grouping. Comments
    // are absent from _tokens. A property reference keeps its familiar variable.property name.
    private string NormalizeProjectionText(int first, int end)
    {
        var text = new System.Text.StringBuilder();
        for (int i = first; i < end; i++)
        {
            var token = _tokens[i];
            if (i > first && token.Type is not (TokenType.Dot or TokenType.RightParen or TokenType.RightBracket or TokenType.Comma) &&
                _tokens[i - 1].Type is not (TokenType.Dot or TokenType.LeftParen or TokenType.LeftBracket)) { text.Append(' '); }
            // Scalar parsing splits a lexical <- into < and unary - in place. Restore the
            // comparison in its column name without inserting tokens (quadratic for chains).
            if (token.Type == TokenType.Minus && token.Start > 0 && _source[token.Start - 1] == '<') { text.Append("< "); }
            text.Append(token.Text);
        }
        return text.ToString();
    }
}
