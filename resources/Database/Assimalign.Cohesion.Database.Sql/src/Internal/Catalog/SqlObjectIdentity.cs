using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

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

    internal static string View(string schema, string view) => "V:" + Quote(schema) + "." + Quote(view);

    internal static string Column(ulong table, string column) => "C:" + table.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Quote(column);

    internal static string Schema(string name) => "S:" + Quote(name);

    internal static string Signature(string name, IEnumerable<string> parameterTypes)
        => Quote(name.ToUpperInvariant()) + "(" + string.Join(",", parameterTypes) + ")";
}
