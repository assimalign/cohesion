using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlQueryExpression ParseQuery()
    {
        var start = Current;
        IReadOnlyList<GqlPathPattern> matches = [];
        IReadOnlyList<GqlPathPattern> creates = [];
        GqlExpression? predicate = null;
        List<string> deletes = [];
        List<GqlProjection> projections = [];
        bool detach = false;
        if (Take("MATCH"))
        {
            matches = ParsePatterns(allowPathVariable: true);
            if (!Failed && Take("WHERE")) { predicate = ParsePredicate(); }
        }
        if (!Failed && (Take("CREATE") || Take("INSERT"))) { creates = ParsePatterns(); }
        else if (!Failed && (Is("DETACH") || Is("DELETE")))
        {
            detach = Take("DETACH");
            Expect("DELETE");
            do { deletes.Add(Identifier()); } while (!Failed && Take(TokenType.Comma));
        }
        if (!Failed && Take("RETURN"))
        {
            do
            {
                string variable = Identifier();
                string? property = Take(TokenType.Dot) ? Identifier(allowKeyword: true) : null;
                string? alias = Take("AS") ? Identifier() : null;
                projections.Add(new GqlProjection(variable, property, alias));
            } while (!Failed && Take(TokenType.Comma));
        }
        if (!Failed && creates.Count == 0 && deletes.Count == 0 && projections.Count == 0)
        {
            Error("GQL0002", "MATCH requires RETURN, INSERT, CREATE, or DELETE.", Current);
        }
        if (!Failed && deletes.Count != 0 && projections.Count != 0)
        {
            Unsupported("RETURN AFTER DELETE", Previous);
        }
        return new GqlQueryExpression(matches, predicate, creates, deletes.AsReadOnly(), detach,
            projections.AsReadOnly(), Span(start, Previous));
    }
}
