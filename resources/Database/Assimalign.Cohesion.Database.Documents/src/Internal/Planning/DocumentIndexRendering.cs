using System;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentIndexRendering
{
    // The canonical local name is independent of the name used in CREATE INDEX.
    internal static string Key(DocumentIndexKey key, string? sourceName = null) => key.ArraySource is null ?
        Qualifier(key.Path, sourceName) is string prefix ? Path(key.Path, prefix) : key.Path :
        $"DISTINCT ARRAY {Element(key.Path)} FOR v IN {Path(key.ArraySource, Qualifier(key.ArraySource, sourceName) ?? string.Empty)}" +
        (key.When is null ? string.Empty : $" WHEN {Predicate(key.When)}") + " END";

    internal static string Key(DocumentIndexMetadata index, int ordinal, string? sourceName = null) => Key(index.Keys[ordinal], sourceName) +
        (ordinal == 0 && index.IncludeMissing ? " INCLUDE MISSING" : string.Empty);

    private static string Element(string path) => path.Length == 0 ? "v" : Path(path, "v");

    // Catalog paths are document-relative. Render an extra receiver when the first
    // field would otherwise become the collection alias on CREATE INDEX replay.
    private static string? Qualifier(string path, string? sourceName)
    {
        if (sourceName is not null)
        {
            foreach (var segment in DocumentIndexKeys.ParsePath(path))
            { return segment is string name && name == sourceName ? Quote(sourceName) : null; }
        }
        return null;
    }

    private static string Path(string path, string prefix)
    {
        var builder = new StringBuilder(prefix);
        foreach (var segment in DocumentIndexKeys.ParsePath(path))
        {
            if (segment is string name)
            {
                bool root = builder.Length == 0;
                if (!root) { builder.Append('.'); }
                builder.Append(root || !PlainIdentifier(name) ? Quote(name) : name);
            }
            else { builder.Append('[').Append(segment).Append(']'); }
        }
        return builder.ToString();
    }

    private static bool PlainIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) { return false; }
        foreach (char character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_')) { return false; }
        }
        foreach (string keyword in OqlLanguageProfile.Instance.Keywords)
        {
            if (name.Equals(keyword, StringComparison.OrdinalIgnoreCase)) { return false; }
        }
        return true;
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
