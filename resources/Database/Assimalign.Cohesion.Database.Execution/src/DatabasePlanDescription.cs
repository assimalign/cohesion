using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>A materialized EXPLAIN result with the same eight columns for every language.</summary>
/// <remarks>
/// Nodes appear in depth-first pre-order. Counts are optional model-defined candidate work.
/// Nonexecuting descriptions never supply observed counts.
/// The shape describes a chosen plan; it does not define a shared operator algebra.
/// </remarks>
public sealed class DatabasePlanDescription : DatabaseResultSet
{
    private static readonly IReadOnlyList<DatabaseColumn> _columns = Array.AsReadOnly<DatabaseColumn>(
    [
        new() { Name = "node_id", Ordinal = 0, Type = DatabaseType.Int32 },
        new() { Name = "parent_id", Ordinal = 1, Type = DatabaseType.Int32, IsNullable = true },
        new() { Name = "operator", Ordinal = 2, Type = DatabaseType.String },
        new() { Name = "target", Ordinal = 3, Type = DatabaseType.String, IsNullable = true },
        new() { Name = "index", Ordinal = 4, Type = DatabaseType.String, IsNullable = true },
        new() { Name = "detail", Ordinal = 5, Type = DatabaseType.String, IsNullable = true },
        new() { Name = "estimated_rows", Ordinal = 6, Type = DatabaseType.Int64, IsNullable = true },
        new() { Name = "actual_rows", Ordinal = 7, Type = DatabaseType.Int64, IsNullable = true },
    ]);
    private readonly DatabasePlanDescriptionRow[] _rows;
    private readonly IReadOnlyList<Diagnostic> _diagnostics;

    private DatabasePlanDescription(IEnumerable<DatabasePlanNode> nodes, IEnumerable<Diagnostic>? diagnostics)
    {
        _rows = nodes.Select(node => new DatabasePlanDescriptionRow(node)).ToArray();
        _diagnostics = Array.AsReadOnly(diagnostics?.ToArray() ?? []);
    }

    /// <summary>Materializes the model's plan nodes in their supplied depth-first pre-order.</summary>
    /// <param name="nodes">The chosen operator tree, already ordered by the model.</param>
    /// <param name="diagnostics">Optional parser or planner diagnostics.</param>
    /// <returns>An ordinary row result containing the description.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> or a node is null.</exception>
    public static DatabasePlanDescription Create(IEnumerable<DatabasePlanNode> nodes, IEnumerable<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return new DatabasePlanDescription(nodes, diagnostics);
    }

    /// <inheritdoc />
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    /// <inheritdoc />
    public override long AffectedCount => -1;
    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    /// <inheritdoc />
    public override IReadOnlyList<DatabaseColumn> Columns => _columns;

    /// <inheritdoc />
    public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
