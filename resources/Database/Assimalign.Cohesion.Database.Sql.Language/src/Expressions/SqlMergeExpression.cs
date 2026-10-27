using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a MERGE statement with ordered conditional actions.</summary>
public sealed class SqlMergeExpression : SqlQueryExpression
{
    internal SqlMergeExpression(SqlTableReference table, SqlFromItem source, SqlExpression condition,
        IReadOnlyList<SqlMergeWhenClause> actions, IReadOnlyList<SqlSelectColumn>? returning, Location? location)
        : base(SqlQueryCommandType.Merge, null, location)
    {
        Table = table;
        Source = source;
        Condition = condition;
        Actions = actions;
        Returning = returning;
    }

    /// <summary>Gets the stored target table and its optional alias.</summary>
    public SqlTableReference Table { get; }

    /// <summary>Gets the USING relation and its joins.</summary>
    public SqlFromItem Source { get; }

    /// <summary>Gets the ON search condition.</summary>
    public SqlExpression Condition { get; }

    /// <summary>Gets the WHEN clauses in written order.</summary>
    public IReadOnlyList<SqlMergeWhenClause> Actions { get; }

    /// <summary>Gets the changed-row projection, or null when absent.</summary>
    public IReadOnlyList<SqlSelectColumn>? Returning { get; }
}
