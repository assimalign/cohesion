using System.Collections.Generic;
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
        else if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function && !IsReturningClause())
        {
            alias = Identifier();
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
        return new OqlSelectExpression(collection, alias, projections.AsReadOnly(), predicate,
            groups.AsReadOnly(), having, ordering.AsReadOnly(), distinct, value, Span(start, Previous));
    }

    private bool IsReturningClause()
    {
        if (!Is("RETURNING")) { return false; }
        var next = _tokens[_position + 1];
        // An implicit source alias must be followed by the end or another clause. Keep
        // RETURNING as that name there; otherwise leave the trailing clause for rejection.
        return next.Type is not (TokenType.Eof or TokenType.Semicolon) &&
            !IsWord(next, "WHERE") && !IsWord(next, "GROUP") && !IsWord(next, "HAVING") &&
            !IsWord(next, "ORDER") && !(IsWord(next, "RETURNING") &&
                _tokens[_position + 2].Type is not (TokenType.Eof or TokenType.Semicolon));
    }
}
