using System.Collections.Generic;

// Deviates from the repo namespace-matches-assembly rule: this stateless catalog identity encoder is shared with SQL so persisted and expected targets use one encoding.
namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

internal static class SqlObjectIdentity
{
    // Every name is one delimited component. Delimiters inside names are data, including quotes.
    // Callers compare identities with the catalog's OrdinalIgnoreCase name rule; receipt state
    // comparisons remain ordinal because they compare an exact preceding durable snapshot.
    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    internal static string Table(string schema, string table) => "T:" + Quote(schema) + "." + Quote(table);

    internal static string Index(string schema, string table, string index)
        => "I:" + Quote(schema) + "." + Quote(table) + "." + Quote(index);

    internal static string Domain(string name) => "D:" + Quote(name);

    internal static string Signature(string name, IEnumerable<string> parameterTypes)
        => Quote(name.ToUpperInvariant()) + "(" + string.Join(",", parameterTypes) + ")";
}
