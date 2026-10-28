using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlExpression ParseExists()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
        Advance();
        if (Current.Type == TokenType.LeftParen)
        { Unsupported("PARENTHESIZED EXISTS", start); return new GqlLiteralExpression(null, Span(start, Current)); }
        if (!Take(TokenType.LeftBrace))
        { Error("CG00013", ErrorMessages.ExistsBodyRequired, Current); return new GqlLiteralExpression(null, Span(start, Current)); }
        var clauses = new List<GqlMatchClause>();
        bool explicitMatch = Is("MATCH") || Is("OPTIONAL");
        do
        {
            bool optional = false;
            if (explicitMatch) { optional = Take("OPTIONAL"); Expect("MATCH"); }
            if (Failed) { break; }
            if (Current.Type == TokenType.RightBrace)
            { Error("CG00013", ErrorMessages.ExistsBodyRequired, Current); break; }
            if (IsExistsQueryClause()) { Unsupported("EXISTS QUERY BODY", Current); break; }
            var patterns = ParsePatterns(allowPathVariable: true);
            GqlExpression? predicate = null;
            if (!Failed && Take("WHERE")) { predicate = ParsePredicate(); }
            clauses.Add(new GqlMatchClause(patterns, predicate, optional));
        } while (!Failed && explicitMatch && (Is("MATCH") || Is("OPTIONAL")));
        if (!Failed && IsExistsQueryClause()) { Unsupported("EXISTS QUERY BODY", Current); }
        if (!Failed) { Expect(TokenType.RightBrace, "'}'"); }
        return new GqlExistsExpression(clauses.AsReadOnly(), Span(start, Previous));
    }

    private bool IsExistsQueryClause() => Is("RETURN") || Is("WITH") || Is("NEXT") || Is("UNION") ||
        Is("CALL") || Is("ORDER") || Is("LIMIT") || Is("OFFSET") || Is("SKIP") || Is("INSERT") ||
        Is("CREATE") || Is("MERGE") || Is("DELETE") || Is("DETACH") || Is("SET") || Is("REMOVE") ||
        Is("LET") || Is("FOR") || Is("UNWIND") || Is("FILTER");
}
