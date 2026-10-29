using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Language;

public static partial class SqlExpressionRenderer
{
    /// <summary>Renders CREATE SEQUENCE without a terminator.</summary>
    /// <param name="query">The sequence declaration.</param>
    /// <returns>Canonical SQL.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    public static string Render(SqlCreateSequenceExpression query)
    { ArgumentNullException.ThrowIfNull(query); return "CREATE SEQUENCE " + (query.IfNotExists ? "IF NOT EXISTS " : "") + SequenceName(query.Sequence) + SequenceOptions(query.Options); }

    /// <summary>Renders ALTER SEQUENCE without a terminator.</summary>
    /// <param name="query">The sequence alteration.</param>
    /// <returns>Canonical SQL.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    public static string Render(SqlAlterSequenceExpression query)
    { ArgumentNullException.ThrowIfNull(query); return "ALTER SEQUENCE " + (query.IfExists ? "IF EXISTS " : "") + SequenceName(query.Sequence) + SequenceOptions(query.Options); }

    /// <summary>Renders DROP SEQUENCE without a terminator.</summary>
    /// <param name="query">The sequence removal.</param>
    /// <returns>Canonical SQL with an explicit dependency policy.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    public static string Render(SqlDropSequenceExpression query)
    { ArgumentNullException.ThrowIfNull(query); return "DROP SEQUENCE " + (query.IfExists ? "IF EXISTS " : "") + string.Join(", ", query.Sequences.Select(SequenceName)) + (query.Cascade ? " CASCADE" : " RESTRICT"); }

    private static string SequenceName(SqlTableReference name)
        => (name.SchemaName is { } schema ? Identifier(schema) + "." : "") + Identifier(name.TableName);

    private static string SequenceOptions(IReadOnlyList<SqlSequenceOption> options)
        => string.Concat(options.Select(option => " " + (option.Name switch
        {
            "AS" => "AS " + option.TypeName,
            "CYCLE" => option.Value == 0 ? "NO CYCLE" : "CYCLE",
            "MINVALUE" or "MAXVALUE" when option.Value is null => "NO " + option.Name,
            "RESTART" when option.Value is null => "RESTART",
            _ => option.Name + (option.Name is "START" or "RESTART" ? " WITH " : option.Name == "INCREMENT" ? " BY " : " ") + option.Value?.ToString(CultureInfo.InvariantCulture),
        })));
}
