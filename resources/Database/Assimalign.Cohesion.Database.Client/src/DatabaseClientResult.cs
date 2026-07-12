using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// The materialized result of one executed statement: columns and rows for
/// row-returning statements, or the affected count for everything else.
/// </summary>
/// <remarks>
/// The MVP client materializes results while draining the wire exchange — the
/// protocol streams rows, but a connection is only reusable once its exchange is
/// fully consumed, so buffering keeps pooling simple. An incremental
/// (`IAsyncEnumerable`) surface arrives with the per-model clients that need it.
/// </remarks>
public sealed class DatabaseClientResult
{
    internal DatabaseClientResult(IReadOnlyList<DatabaseClientColumn> columns, IReadOnlyList<object?[]> rows, long affectedCount)
    {
        Columns = columns;
        Rows = rows;
        AffectedCount = affectedCount;
    }

    /// <summary>
    /// Gets the result columns; empty for statements that return no rows.
    /// </summary>
    public IReadOnlyList<DatabaseClientColumn> Columns { get; }

    /// <summary>
    /// Gets the materialized rows, each with one boxed value per column in
    /// column order; empty for statements that return no rows.
    /// </summary>
    public IReadOnlyList<object?[]> Rows { get; }

    /// <summary>
    /// Gets the number of records the statement affected, or -1 for
    /// row-returning statements.
    /// </summary>
    public long AffectedCount { get; }
}
