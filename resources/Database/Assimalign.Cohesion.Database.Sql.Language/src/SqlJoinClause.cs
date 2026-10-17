using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Represents a JOIN clause in a SELECT statement.
/// </summary>
public sealed class SqlJoinClause
{
    /// <summary>
    /// Initializes a new <see cref="SqlJoinClause"/>.
    /// </summary>
    /// <param name="joinType">The type of join.</param>
    /// <param name="table">The table being joined.</param>
    /// <param name="condition">The ON condition, if any.</param>
    /// <param name="usingColumns">The ordered USING column names; empty for ON or CROSS.</param>
    internal SqlJoinClause(SqlJoinType joinType, SqlTableReference table, SqlExpression? condition, IReadOnlyList<string> usingColumns)
    {
        JoinType = joinType;
        Table = table;
        Condition = condition;
        UsingColumns = usingColumns;
    }

    /// <summary>
    /// Gets the type of join (INNER, LEFT, RIGHT, FULL, CROSS).
    /// </summary>
    public SqlJoinType JoinType { get; }

    /// <summary>
    /// Gets the table being joined.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets the ON condition expression, if present.
    /// </summary>
    public SqlExpression? Condition { get; }

    /// <summary>Gets the ordered USING column names; empty for ON or CROSS.</summary>
    public IReadOnlyList<string> UsingColumns { get; }
}
