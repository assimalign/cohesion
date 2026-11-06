using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

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
    /// <param name="table">The named or derived relation being joined.</param>
    /// <param name="condition">The ON condition, if any.</param>
    /// <param name="usingColumns">The ordered explicit USING column names.</param>
    /// <param name="isNatural">Whether the join derives its USING names.</param>
    /// <param name="location">The complete source span.</param>
    /// <param name="keywordLocation">The join keyword span.</param>
    internal SqlJoinClause(SqlJoinType joinType, SqlRelationReference table, SqlExpression? condition, IReadOnlyList<string> usingColumns,
        bool isNatural = false, Location? location = null, Location? keywordLocation = null)
    {
        JoinType = joinType;
        Table = table;
        Condition = condition;
        UsingColumns = usingColumns;
        IsNatural = isNatural;
        Location = location;
        KeywordLocation = keywordLocation;
    }

    /// <summary>
    /// Gets the type of join (INNER, LEFT, RIGHT, FULL, CROSS).
    /// </summary>
    public SqlJoinType JoinType { get; }

    /// <summary>Gets whether the join derives its USING names from the inputs.</summary>
    public bool IsNatural { get; }

    /// <summary>Gets the source span of the join through its right input and optional condition.</summary>
    public Location? Location { get; }

    /// <summary>Gets the source span of the join keywords, including NATURAL when present.</summary>
    public Location? KeywordLocation { get; }

    /// <summary>
    /// Gets the named or derived relation being joined.
    /// </summary>
    public SqlRelationReference Table { get; }

    /// <summary>
    /// Gets the ON condition expression, if present.
    /// </summary>
    public SqlExpression? Condition { get; }

    /// <summary>Gets the ordered explicit USING names; empty when absent, including NATURAL joins.</summary>
    public IReadOnlyList<string> UsingColumns { get; }
}
