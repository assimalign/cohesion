using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents CREATE TABLE AS with inferred output columns.</summary>
public sealed class SqlCreateTableAsExpression : SqlQueryExpression
{
    internal SqlCreateTableAsExpression(SqlTableReference table, IReadOnlyList<string> columns,
        SqlQueryBodyExpression query, bool ifNotExists, bool withData, Location location)
        : base(SqlQueryCommandType.Create, null, location)
    {
        Table = table;
        Columns = columns;
        Query = query;
        IfNotExists = ifNotExists;
        WithData = withData;
    }

    /// <summary>Gets the destination table.</summary>
    public SqlTableReference Table { get; }

    /// <summary>Gets the optional complete output name list.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the query whose output defines the table.</summary>
    public SqlQueryBodyExpression Query { get; }

    /// <summary>Gets whether an existing destination is left unchanged.</summary>
    public bool IfNotExists { get; }

    /// <summary>Gets whether the query is executed to populate the table.</summary>
    public bool WithData { get; }

    private protected override int ExpressionTreeDepth => Query.ExpressionDepth;
}
