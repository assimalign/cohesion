using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Documents.Catalog;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentIndexRendering
{
    // The canonical local name is independent of the name used in CREATE INDEX.
    internal static string Key(DocumentIndexKey key) => key.ArraySource is null ? key.Path :
        $"DISTINCT ARRAY {Element(key.Path)} FOR v IN {Source(key.ArraySource)}" +
        (key.When is null ? string.Empty : $" WHEN {Predicate(key.When)}") + " END";

    private static string Element(string path) => "v" + (path.Length == 0 || path[0] == '[' ? string.Empty : ".") + path;

    private static string Source(string path)
    {
        // Quote the root name so a contextual/keyword-shaped property round-trips.
        if (path.StartsWith("['", StringComparison.Ordinal))
        {
            int end = 2;
            var name = new System.Text.StringBuilder();
            while (end < path.Length)
            {
                char next = path[end++];
                if (next != '\'') { name.Append(next); continue; }
                if (end < path.Length && path[end] == '\'') { name.Append('\''); end++; continue; }
                return Quote(name.ToString()) + path[(end + 1)..];
            }
        }
        int split = path.IndexOfAny(['.', '[']);
        return split < 0 ? Quote(path) : Quote(path[..split]) + path[split..];
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Predicate(DocumentIndexPredicate predicate) => predicate.Operation switch
    {
        DocumentIndexPredicateKind.And => $"({Predicate(predicate.Left!)} AND {Predicate(predicate.Right!)})",
        DocumentIndexPredicateKind.Or => $"({Predicate(predicate.Left!)} OR {Predicate(predicate.Right!)})",
        DocumentIndexPredicateKind.Not => $"NOT ({Predicate(predicate.Left!)})",
        DocumentIndexPredicateKind.IsNull => Element(predicate.Path!) + " IS NULL",
        DocumentIndexPredicateKind.IsMissing => Element(predicate.Path!) + " IS MISSING",
        _ => Element(predicate.Path!) + " " + (predicate.Comparison switch
        {
            DocumentIndexComparison.Equal => "=", DocumentIndexComparison.NotEqual => "!=",
            DocumentIndexComparison.LessThan => "<", DocumentIndexComparison.LessThanOrEqual => "<=",
            DocumentIndexComparison.GreaterThan => ">", _ => ">=",
        }) + " " + (predicate.Value switch
        {
            null => "NULL", true => "TRUE", false => "FALSE",
            string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
            _ => Convert.ToString(predicate.Value, CultureInfo.InvariantCulture),
        }),
    };
}
