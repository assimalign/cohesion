using System.Collections.Generic;
using System.Diagnostics;

using Assimalign.Cohesion.Database.Sql.Language.Internal;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>The unique-key arbitration and action of an INSERT.</summary>
[DebuggerDisplay("{TargetKind}: {Action}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class SqlOnConflictClause
{
    // Reuse the action-WHERE reference slot. Existing conflict syntax keeps its
    // object size and allocations; only the new form needs a predicate pair.
    private readonly object? _predicateState;

    internal SqlOnConflictClause(SqlOnConflictTargetKind targetKind, IReadOnlyList<string> columns,
        string? constraintName, SqlOnConflictAction action, IReadOnlyList<SqlAssignment> assignments, SqlExpression? where, SqlExpression? targetWhere = null)
    {
        TargetKind = targetKind;
        Columns = columns;
        ConstraintName = constraintName;
        Action = action;
        Assignments = assignments;
        _predicateState = targetWhere is null ? where : new SqlOnConflictPredicates(targetWhere, where);
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
    /// <summary>Gets the optional predicate used to infer partial unique indexes.</summary>
    public SqlExpression? TargetWhere => (_predicateState as SqlOnConflictPredicates)?.Target;
    /// <summary>Gets the optional DO UPDATE filter.</summary>
    public SqlExpression? Where => _predicateState is SqlOnConflictPredicates predicates ? predicates.Action : _predicateState as SqlExpression;
    /// <summary>Gets the proposed-row relation reference supplied by the parser for binding.</summary>
    public SqlRelationReference Excluded { get; }

    private sealed class DebugView
    {
        private readonly SqlOnConflictClause _clause;

        public DebugView(SqlOnConflictClause clause) { _clause = clause; }

        public SqlOnConflictTargetKind TargetKind => _clause.TargetKind;
        public IReadOnlyList<string> Columns => _clause.Columns;
        public string? ConstraintName => _clause.ConstraintName;
        public SqlOnConflictAction Action => _clause.Action;
        public IReadOnlyList<SqlAssignment> Assignments => _clause.Assignments;
        public SqlExpression? TargetWhere => _clause.TargetWhere;
        public SqlExpression? Where => _clause.Where;
        public SqlRelationReference Excluded => _clause.Excluded;
    }
}
