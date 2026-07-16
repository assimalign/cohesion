using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The bound plan produced by <see cref="SqlPlanner"/>: the AST resolved against the
/// catalog into executable shape. Rule-based and deliberately simple — a cost-based
/// planner replaces the binding internals later without changing the executor seam.
/// </summary>
internal abstract record SqlPlan;

/// <summary>One projected output column of a SELECT.</summary>
/// <param name="Name">The output column name (alias, column name, or a synthesized name).</param>
/// <param name="ColumnOrdinal">The source column ordinal for pass-through projections; null for computed ones.</param>
/// <param name="Expression">The computed expression; null for pass-through projections.</param>
/// <param name="Type">The declared output type (<see cref="DatabaseType.Null"/> when not statically known).</param>
internal sealed record SqlProjection(string Name, int? ColumnOrdinal, SqlExpression? Expression, DatabaseType Type);

internal sealed record SqlSelectPlan(
    SqlCatalogTable Table,
    IReadOnlyList<SqlProjection> Projections,
    SqlExpression? Where,
    IReadOnlyList<SqlOrderByColumn> OrderBy,
    long? Limit,
    long? Offset,
    bool IsDistinct,
    bool IsCountStar,
    SqlAccessPath Access) : SqlPlan;

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

internal sealed record SqlInsertPlan(
    SqlCatalogTable Table,
    IReadOnlyList<int> TargetOrdinals,
    IReadOnlyList<IReadOnlyList<SqlExpression>> Rows) : SqlPlan;

internal sealed record SqlUpdatePlan(
    SqlCatalogTable Table,
    IReadOnlyList<(int Ordinal, SqlExpression Value)> Assignments,
    SqlExpression? Where) : SqlPlan;

internal sealed record SqlDeletePlan(SqlCatalogTable Table, SqlExpression? Where) : SqlPlan;

internal sealed record SqlCreateTablePlan(
    string Schema,
    string Name,
    IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    bool IfNotExists) : SqlPlan;

internal sealed record SqlDropTablePlan(string Schema, string Name, bool IfExists) : SqlPlan;

internal sealed record SqlAddColumnPlan(string Schema, string Name, SqlCatalogColumn Column) : SqlPlan;

internal sealed record SqlDropColumnPlan(string Schema, string Name, string ColumnName) : SqlPlan;

internal sealed record SqlCreateIndexPlan(
    SqlCatalogTable Table,
    string IndexName,
    IReadOnlyList<string> ColumnNames,
    bool IsUnique,
    bool IfNotExists) : SqlPlan;

internal sealed record SqlDropIndexPlan(SqlCatalogTable Table, string IndexName, bool IfExists) : SqlPlan;
