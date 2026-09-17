using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlSelectExpression ParseSelect()
    {
        var start = Current;
        Advance();
        List<OqlProjection> projections = [];
        do
        {
            var expression = ParseExpression();
            string? projectionAlias = Take("AS") ? Identifier() : null;
            projections.Add(new OqlProjection(expression, projectionAlias));
        } while (!Failed && Take(TokenType.Comma));
        Expect("FROM");
        string collection = Identifier();
        string? alias = null;
        if (Take("AS"))
        {
            alias = Identifier();
        }
        else if (Current.Type is TokenType.Identifier or TokenType.QuotedIdentifier)
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
            groups.AsReadOnly(), having, ordering.AsReadOnly(), Span(start, Previous));
    }
}
