using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentSourceName
{
    internal static string Of(OqlSelectExpression query) => Of(query.Collection, query.Alias);

    internal static bool IsImplicitCollection(OqlSelectExpression query, string name)
    {
        if (query.Source is null && query.Alias is null && Of(query) == name) { return true; }
        foreach (var binding in query.Bindings)
        { if (binding is OqlJoinExpression { Alias: null } && binding.Variable == name) { return true; } }
        return false;
    }

    internal static string Of(string collection, string? alias)
        // A stored collection keeps its complete name, including dots in a quoted name.
        // Only a reserved system source has a qualifier rather than a collection-name dot.
        => alias ?? (DocumentSystemCollections.Find(collection) is not null
            ? collection[(collection.LastIndexOf('.') + 1)..] : collection);
}
