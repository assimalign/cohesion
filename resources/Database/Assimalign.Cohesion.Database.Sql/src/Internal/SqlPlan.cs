using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The bound plan produced by <see cref="SqlPlanner"/>: the AST resolved against the
/// catalog into executable shape. Rule-based and deliberately simple — a cost-based
/// planner replaces the binding internals later without changing the executor seam.
/// Every scalar expression a plan evaluates is a <see cref="SqlBoundExpression"/> the
/// planner compiled once; the executor never resolves a name or a collation per row.
/// </summary>
internal abstract record SqlPlan;

/// <summary>Creates an empty SQL namespace.</summary>
internal sealed record SqlCreateSchemaPlan(string Name, bool IfNotExists) : SqlPlan;

/// <summary>Drops a SQL namespace and optionally its dependent objects.</summary>
internal sealed record SqlDropSchemaPlan(string Name, bool IfExists, bool Cascade) : SqlPlan;

/// <summary>The typed output contract of every executable relation.</summary>
internal abstract record SqlRelationPlan : SqlPlan
{
    internal abstract IReadOnlyList<SqlProjection> OutputProjections { get; }
    internal abstract IReadOnlyList<SqlCatalogColumn> OutputColumns { get; }
    internal virtual IReadOnlyList<SqlRelationBinding>? OutputBindings => null;
}

/// <summary>
/// Groups the filtered rows of an input plan. A grouped row holds the group keys, then the
/// aggregate results, then the projected outputs; <c>HAVING</c>, the projections and
/// <c>ORDER BY</c> are bound to those slots (<see cref="SqlBoundSlot"/>), never to an
/// arbitrary representative row.
/// </summary>
internal sealed record SqlGroupPlan(
    SqlPlan Input,
    IReadOnlyList<SqlCatalogColumn> SourceColumns,
    IReadOnlyList<SqlRelationBinding>? Bindings,
    IReadOnlyList<SqlBoundKey> Keys,
    IReadOnlyList<SqlGroupAggregate> Aggregates,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Having,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    long? Limit,
    long? Offset,
    bool IsDistinct) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => SourceColumns;
    internal override IReadOnlyList<SqlRelationBinding>? OutputBindings => Bindings;
}

/// <summary>
/// One distinct aggregate call of a grouping plan, bound to the overload it resolved to, so the
/// executor creates one accumulator of that function per group instead of finding it by name.
/// </summary>
/// <remarks>
/// A class with get-only properties, not a positional record, as <see cref="SqlBoundCall"/> is:
/// <see cref="FirstTarget"/> is derived from <see cref="Targets"/> once, at construction, and a
/// record's <c>with</c> would copy it beside a replaced <see cref="Targets"/>, so the one-argument
/// add would convert to the old target while the buffered add used the new one.
/// </remarks>
internal sealed class SqlGroupAggregate
{
    /// <summary>Initializes a bound aggregate call.</summary>
    /// <param name="call">The aggregate call as written.</param>
    /// <param name="function">The overload the call resolved to.</param>
    /// <param name="arguments">The bound arguments over the input row; empty for <c>name(*)</c>, as in <c>COUNT(*)</c>.</param>
    /// <param name="targets">The storage type each argument converts to before it is added, or null when none converts.</param>
    /// <param name="collation">The collation the arguments compare under, which the function's context carries (<c>MIN</c>, <c>MAX</c>).</param>
    /// <param name="database">The database whose statement runs the aggregate.</param>
    internal SqlGroupAggregate(SqlFunctionCallExpression call, SqlAggregateFunction function,
        SqlBoundExpression[] arguments, DatabaseType[]? targets, SqlBoundCollation collation, DatabaseName database)
    {
        Call = call;
        Function = function;
        Arguments = arguments;
        Targets = targets;
        FirstTarget = targets is { Length: > 0 } ? targets[0] : DatabaseType.Null;
        Collation = collation;
        Database = database;
    }

    /// <summary>Gets the aggregate call as written.</summary>
    internal SqlFunctionCallExpression Call { get; }

    /// <summary>Gets the overload the call resolved to.</summary>
    internal SqlAggregateFunction Function { get; }

    /// <summary>Gets the bound arguments over the input row; empty for <c>name(*)</c>, as in <c>COUNT(*)</c>.</summary>
    internal SqlBoundExpression[] Arguments { get; }

    /// <summary>Gets the storage type each argument converts to before it is added, or null when none converts.</summary>
    internal DatabaseType[]? Targets { get; }

    /// <summary>
    /// Gets the storage type the first argument converts to (<see cref="DatabaseType.Null"/> when it
    /// converts to none), read by every row of a one-argument call without testing the array.
    /// </summary>
    internal DatabaseType FirstTarget { get; }

    /// <summary>Gets the collation the arguments compare under, which the function's context carries (<c>MIN</c>, <c>MAX</c>).</summary>
    internal SqlBoundCollation Collation { get; }

    /// <summary>Gets the database whose statement runs the aggregate.</summary>
    internal DatabaseName Database { get; }
}

/// <summary>A grouping key bound over the input row, with the collation its values are grouped under.</summary>
/// <param name="Value">The bound key expression.</param>
/// <param name="Collation">The collation the key's values compare and hash under.</param>
internal sealed record SqlBoundKey(SqlBoundExpression Value, SqlBoundCollation Collation);

/// <summary>One bound <c>ORDER BY</c> key.</summary>
/// <param name="Key">The bound key, over the source row, or over the row extended by the completed outputs when the plan orders by them.</param>
/// <param name="IsDescending">Whether the key sorts descending.</param>
/// <param name="Collation">The collation the key's values compare under.</param>
internal sealed record SqlBoundOrdering(SqlBoundExpression Key, bool IsDescending, SqlBoundCollation Collation);

/// <summary>One projected output column of a SELECT.</summary>
/// <param name="Name">The output column name (alias, column name, or a synthesized name).</param>
/// <param name="ColumnOrdinal">The source column ordinal for pass-through projections; null for computed ones.</param>
/// <param name="Expression">The computed expression; null for pass-through projections. Planning reads it for types and collations.</param>
/// <param name="Type">The declared output type (<see cref="DatabaseType.Null"/> when not statically known).</param>
internal sealed record SqlProjection(string Name, int? ColumnOrdinal, SqlExpression? Expression, DatabaseType Type)
{
    /// <summary>Gets the bound computed expression the executor evaluates; null for pass-through projections.</summary>
    internal SqlBoundExpression? Value { get; init; }

    internal int? MaxLength { get; init; }

    /// <summary>Gets the collation <c>DISTINCT</c> compares and hashes the output's values under.</summary>
    internal SqlBoundCollation Collation { get; init; }

    /// <summary>Gets provenance needed only while binding a derived output; absent on ordinary stored queries.</summary>
    internal SqlProjectionMetadata? Metadata { get; init; }

    /// <summary>
    /// Gets whether a grouped output may be NULL: false only for a bare call of an aggregate that
    /// never returns NULL, such as <c>COUNT</c>, which counts 0 for an empty group.
    /// </summary>
    internal bool IsNullable { get; init; } = true;
}

/// <summary>Projected expression metadata retained across derived materialization.</summary>
internal sealed record SqlProjectionMetadata(SqlCollationCandidate Collation, SqlCatalogColumn? SourceColumn,
    DatabaseTypeInfo? TypeInfo = null, string? DomainName = null);

internal sealed record SqlSetOperand(SqlPlan Query, IReadOnlyList<SqlBoundExpression> Values);

/// <summary>A materialized set operation, with compatible UNION chains stored as N inputs.</summary>
internal sealed record SqlSetOperationPlan(
    List<SqlSetOperand> Operands, SqlSetOperator Operator, bool IsAll,
    IReadOnlyList<SqlProjection> Projections, IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<SqlBoundOrdering> OrderBy, long? Limit, long? Offset) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Columns;
}

internal sealed record SqlSelectPlan(
    SqlCatalogTable Table,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    long? Limit,
    long? Offset,
    bool IsDistinct,
    SqlAccessPath Access,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections = null) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Table.Columns;
}

/// <summary>A relation's source, qualifier and position in a joined row.</summary>
internal sealed record SqlRelationBinding(SqlRelationSource Source, SqlRelationReference Reference, int Offset)
{
    // USING hides only unqualified right columns, never their qualified references.
    internal HashSet<int> HiddenColumns { get; } = [];

    /// <summary>Merged USING expressions keyed by their preserved physical column ordinal.</summary>
    internal Dictionary<int, SqlUsingColumn>? UsingColumns { get; set; }
}

/// <summary>A USING output's metadata and bound value, independent of qualified physical columns.</summary>
internal sealed record SqlUsingColumn(SqlCatalogColumn Column, SqlBoundExpression Value);

/// <summary>
/// A left-deep N-relation join. Physical slots retain FROM-then-JOIN order even
/// when the selected index reverses which relation drives the nested loop.
/// </summary>
internal sealed record SqlJoinPlan(
    IReadOnlyList<SqlRelationBinding> Bindings,
    IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<SqlJoinStep> Steps,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    long? Limit,
    long? Offset,
    bool IsDistinct,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections = null) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => Columns;
    internal override IReadOnlyList<SqlRelationBinding>? OutputBindings => Bindings;
}

/// <summary>One written join step; comma items introduce unconditional CROSS steps.</summary>
internal sealed record SqlJoinStep(int Binding, SqlJoinType Kind, SqlBoundExpression? Condition, SqlJoinIndexPath? Access);

/// <summary>
/// A correlated equality-prefix seek into one input, with probe ordinals in
/// the accumulated physical row and the inner columns of the seek prefix, resolved
/// once when the statement is planned rather than by name for every outer row.
/// A null path means a buffered nested-loop scan.
/// </summary>
internal sealed record SqlJoinIndexPath(
    int InnerBinding,
    SqlCatalogIndex Index,
    IReadOnlyList<int> OuterOrdinals,
    IReadOnlyList<SqlCatalogColumn> InnerColumns);

/// <summary>A catalog projection with no storage identity or physical access path.</summary>
internal sealed record SqlSystemViewPlan(
    SqlSystemViewDefinition View,
    IReadOnlyList<SqlProjection> Projections,
    SqlBoundExpression? Where,
    IReadOnlyList<SqlBoundOrdering> OrderBy,
    long? Limit,
    long? Offset,
    bool IsDistinct,
    IReadOnlyDictionary<SqlExpression, int>? OrderByProjections = null) : SqlRelationPlan
{
    internal override IReadOnlyList<SqlProjection> OutputProjections => Projections;
    internal override IReadOnlyList<SqlCatalogColumn> OutputColumns => View.Columns;
}

/// <summary>
/// How a SELECT reaches its table's rows — the seek node the thin IR gained when
/// the planner adopted secondary indexes. A closed family: the executor drives
/// exactly these shapes and nothing else.
/// </summary>
internal abstract record SqlAccessPath;

/// <summary>The per-object table scan (the fallback for everything non-sargable).</summary>
internal sealed record SqlScanPath : SqlAccessPath
{
    internal static SqlScanPath Instance { get; } = new();
}

/// <summary>
/// An index seek: an equality prefix over the index's leading key columns
/// (plan-time-evaluated, storage-coerced values) plus an optional range bound on
/// the next key column. The full WHERE stays the residual predicate — a seek only
/// narrows the candidate set, so re-evaluating everything keeps seek results
/// exactly equivalent to the scan they replace.
/// </summary>
/// <param name="Index">The chosen index's catalog description.</param>
/// <param name="EqualityValues">The equality prefix values, one per leading key column, coerced to storage types.</param>
/// <param name="Lower">The optional lower bound on the key column after the prefix.</param>
/// <param name="Upper">The optional upper bound on the key column after the prefix.</param>
internal sealed record SqlIndexSeekPath(
    SqlCatalogIndex Index,
    IReadOnlyList<object?> EqualityValues,
    SqlSeekBound? Lower,
    SqlSeekBound? Upper) : SqlAccessPath;

/// <summary>One endpoint of a seek's range component.</summary>
/// <param name="Value">The bound's value, coerced to the key column's storage type.</param>
/// <param name="Inclusive">Whether the bound itself is included.</param>
internal readonly record struct SqlSeekBound(object? Value, bool Inclusive);

/// <summary>An <c>INSERT ... VALUES</c>: each row's values bound with no columns in scope.</summary>
internal sealed record SqlInsertPlan(
    SqlCatalogTable Table,
    IReadOnlyList<int> TargetOrdinals,
    IReadOnlyList<SqlBoundExpression[]> Rows) : SqlPlan;

internal sealed record SqlUpdatePlan(
    SqlCatalogTable Table,
    IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> Assignments,
    SqlBoundExpression? Where) : SqlPlan;

internal sealed record SqlDeletePlan(SqlCatalogTable Table, SqlBoundExpression? Where) : SqlPlan;

internal sealed record SqlCreateTablePlan(
    string Schema,
    string Name,
    IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    bool IfNotExists,
    IReadOnlyList<SqlConstraintDefinition> Constraints) : SqlPlan;

internal sealed record SqlDropTablePlan(string Schema, string Name, bool IfExists) : SqlPlan;

internal sealed record SqlAddColumnPlan(string Schema, string Name, SqlCatalogColumn Column, IReadOnlyList<SqlConstraintDefinition> Constraints) : SqlPlan;

internal sealed record SqlRenamePlan(SqlCatalogTable Table, string? ColumnName, string NewName) : SqlPlan;

internal sealed record SqlAlterColumnPlan(SqlCatalogTable Table, SqlCatalogColumn Column, bool AllowDefaultChange = false) : SqlPlan;

internal sealed record SqlAddConstraintPlan(SqlCatalogTable Table, SqlConstraintDefinition Constraint) : SqlPlan;

internal sealed record SqlDropConstraintPlan(SqlCatalogTable Table, string ConstraintName) : SqlPlan;

internal sealed record SqlDropColumnPlan(string Schema, string Name, string ColumnName) : SqlPlan;

internal sealed record SqlCreateIndexPlan(
    SqlCatalogTable Table,
    string IndexName,
    IReadOnlyList<string> ColumnNames,
    bool IsUnique,
    bool IfNotExists) : SqlPlan;

internal sealed record SqlDropIndexPlan(SqlCatalogTable Table, string IndexName, bool IfExists) : SqlPlan;

/// <summary>A database-scoped SQL function or domain DDL statement.</summary>
internal sealed record SqlDefinitionPlan(SqlQueryExpression Expression) : SqlPlan;
