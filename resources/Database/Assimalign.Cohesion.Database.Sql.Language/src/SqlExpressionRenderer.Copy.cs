using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Language;

public static partial class SqlExpressionRenderer
{
    /// <summary>Renders a table COPY TO with explicit resolved options.</summary>
    /// <param name="query">The parsed export.</param>
    /// <returns>Canonical COPY SQL without a terminator.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    public static string Render(SqlCopyToExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var options = query.Options;
        static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        var values = new List<string> { "FORMAT " + (options.IsCsv ? "csv" : "text"), "DELIMITER " + Literal(options.Delimiter.ToString()),
            "NULL " + Literal(options.NullString), "HEADER " + (options.Header ? "TRUE" : "FALSE") };
        if (options.IsCsv)
        {
            values.Add("QUOTE " + Literal(options.Quote.ToString()));
            values.Add("ESCAPE " + Literal(options.Escape.ToString()));
            if (options.ForceQuoteAll) { values.Add("FORCE_QUOTE *"); }
            else if (options.ForceQuote.Count > 0) { values.Add("FORCE_QUOTE (" + string.Join(", ", options.ForceQuote.Select(Identifier)) + ")"); }
        }
        return "COPY " + SequenceName(query.Table) + (query.Columns.Count == 0 ? "" : " (" + string.Join(", ", query.Columns.Select(Identifier)) + ")") +
            " TO STDOUT WITH (" + string.Join(", ", values) + ")";
    }
}
