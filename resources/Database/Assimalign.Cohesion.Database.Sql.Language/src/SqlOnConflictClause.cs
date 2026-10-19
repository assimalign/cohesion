using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>The unique-key arbitration and action of an INSERT.</summary>
public sealed class SqlOnConflictClause
{
    internal SqlOnConflictClause(SqlOnConflictTargetKind targetKind, IReadOnlyList<string> columns,
        string? constraintName, SqlOnConflictAction action, IReadOnlyList<SqlAssignment> assignments, SqlExpression? where)
    {
        TargetKind = targetKind;
        Columns = columns;
        ConstraintName = constraintName;
        Action = action;
        Assignments = assignments;
        Where = where;
        Excluded = new SqlTableReference("EXCLUDED", null, "EXCLUDED");
    }

    /// <summary>Gets how the arbiter indexes are selected.</summary>
    public SqlOnConflictTargetKind TargetKind { get; }
    /// <summary>Gets the inference columns, empty when no column target is specified.</summary>
    public IReadOnlyList<string> Columns { get; }
    /// <summary>Gets the named unique index or constraint, if specified.</summary>
    public string? ConstraintName { get; }
    /// <summary>Gets the conflict action.</summary>
    public SqlOnConflictAction Action { get; }
    /// <summary>Gets the DO UPDATE assignments, empty for DO NOTHING.</summary>
    public IReadOnlyList<SqlAssignment> Assignments { get; }
    /// <summary>Gets the optional DO UPDATE filter.</summary>
    public SqlExpression? Where { get; }
    /// <summary>Gets the proposed-row relation reference supplied by the parser for binding.</summary>
    public SqlRelationReference Excluded { get; }
}

/// <summary>How ON CONFLICT selects unique indexes.</summary>
public enum SqlOnConflictTargetKind
{
    /// <summary>All unique indexes (DO NOTHING only).</summary>
    None,
    /// <summary>Every unique index with exactly the specified column set.</summary>
    Columns,
    /// <summary>A unique index named by ON CONSTRAINT.</summary>
    Constraint,
}

/// <summary>The action taken for an arbiter conflict.</summary>
public enum SqlOnConflictAction
{
    /// <summary>Skip the proposed row.</summary>
    DoNothing,
    /// <summary>Update the conflicting row.</summary>
    DoUpdate,
}
