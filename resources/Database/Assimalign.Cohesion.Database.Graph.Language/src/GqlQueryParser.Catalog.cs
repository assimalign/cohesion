using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlQueryExpression ParseCatalog()
    {
        var start = Current;
        Expect("SHOW");
        GqlCatalogSurface? surface = null;
        if (Take("LABELS")) { surface = GqlCatalogSurface.Labels; }
        else if (Take("RELATIONSHIP")) { Expect("TYPES"); surface = GqlCatalogSurface.RelationshipTypes; }
        else if (Take("PROPERTY")) { Expect("KEYS"); surface = GqlCatalogSurface.PropertyKeys; }
        else if (Take("INDEXES")) { surface = GqlCatalogSurface.Indexes; }
        else if (Take("OBJECT")) { Expect("OWNERSHIP"); surface = GqlCatalogSurface.ObjectOwnership; }
        else { Unsupported("SHOW " + Current.Text, Current); }
        return new GqlQueryExpression([], null, [], [], false, [], Span(start, Previous)) { CatalogSurface = surface };
    }

    private void FindCatalogMutation()
    {
        foreach (var token in _tokens)
        {
            if (token.Type is TokenType.String or TokenType.QuotedIdentifier or TokenType.Comment) { continue; }
            if (token.Text.ToUpperInvariant() is "INSERT" or "CREATE" or "DELETE" or "DETACH" or "SET" or "REMOVE" or "MERGE" or "DROP" or "ALTER")
            {
                Error("GQL0007", "Graph catalog introspection is read-only.", token);
                return;
            }
        }
    }
}
