using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents one ordered MERGE action and its optional search condition.</summary>
public sealed class SqlMergeWhenClause
{
    internal SqlMergeWhenClause(SqlMergeMatchKind matchKind, SqlExpression? condition, SqlMergeAction action,
        IReadOnlyList<SqlAssignment> assignments, IReadOnlyList<string>? columns,
        IReadOnlyList<SqlExpression>? values, bool defaultValues, bool explicitByTarget, Location location)
    {
        MatchKind = matchKind;
        Condition = condition;
        Action = action;
        Assignments = assignments;
        Columns = columns;
        Values = values;
        IsDefaultValues = defaultValues;
        IsByTargetExplicit = explicitByTarget;
        Location = location;
    }

    /// <summary>Gets which candidate rows this clause considers.</summary>
    public SqlMergeMatchKind MatchKind { get; }
    /// <summary>Gets the optional AND condition.</summary>
    public SqlExpression? Condition { get; }
    /// <summary>Gets the selected operation.</summary>
    public SqlMergeAction Action { get; }
    /// <summary>Gets UPDATE assignments, or an empty list for other operations.</summary>
    public IReadOnlyList<SqlAssignment> Assignments { get; }
    /// <summary>Gets INSERT target names, or null for the table's column order.</summary>
    public IReadOnlyList<string>? Columns { get; }
    /// <summary>Gets the INSERT value row, or null for other forms.</summary>
    public IReadOnlyList<SqlExpression>? Values { get; }
    /// <summary>Gets whether INSERT specifies DEFAULT VALUES.</summary>
    public bool IsDefaultValues { get; }
    /// <summary>Gets whether NOT MATCHED explicitly spells BY TARGET.</summary>
    public bool IsByTargetExplicit { get; }
    /// <summary>Gets the source span from WHEN through the end of this action.</summary>
    public Location Location { get; }
}

/// <summary>Identifies the candidate class of a MERGE WHEN clause.</summary>
public enum SqlMergeMatchKind
{
    /// <summary>Both a target row and a source row exist.</summary>
    Matched,
    /// <summary>A source row has no matching target row.</summary>
    NotMatchedByTarget,
    /// <summary>A target row has no matching source row.</summary>
    NotMatchedBySource,
}

/// <summary>Identifies a MERGE action.</summary>
public enum SqlMergeAction
{
    /// <summary>Replace selected target column values.</summary>
    Update,
    /// <summary>Remove the target row.</summary>
    Delete,
    /// <summary>Insert a new target row.</summary>
    Insert,
    /// <summary>Leave the candidate unchanged.</summary>
    DoNothing,
}
