using System;
using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// The materialized result of a row-returning SQL command: its typed columns and rows.
/// </summary>
/// <remarks>
/// The typed set is built while draining the wire exchange, mirroring the shared
/// client core's materialization. Column names resolve to ordinals once and every
/// row shares that lookup, so name-based access across a large set stays cheap.
/// </remarks>
public sealed class SqlResultSet : IReadOnlyList<SqlRow>
{
    private readonly IReadOnlyList<SqlRow> _rows;

    private SqlResultSet(IReadOnlyList<SqlColumn> columns, IReadOnlyList<SqlRow> rows)
    {
        Columns = columns;
        _rows = rows;
    }

    /// <summary>
    /// Gets the result columns in ordinal order.
    /// </summary>
    public IReadOnlyList<SqlColumn> Columns { get; }

    /// <summary>
    /// Gets the number of rows in the result set.
    /// </summary>
    public int Count => _rows.Count;

    /// <summary>
    /// Gets the row at the given index.
    /// </summary>
    /// <param name="index">The zero-based row index.</param>
    /// <returns>The row.</returns>
    public SqlRow this[int index] => _rows[index];

    /// <inheritdoc />
    public IEnumerator<SqlRow> GetEnumerator() => _rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Projects the shared client core's materialized result into a typed result set.
    /// </summary>
    /// <param name="result">The materialized core result.</param>
    /// <returns>The typed result set.</returns>
    internal static SqlResultSet FromClientResult(DatabaseClientResult result)
    {
        var columns = new SqlColumn[result.Columns.Count];
        var ordinals = new Dictionary<string, int>(result.Columns.Count, StringComparer.Ordinal);

        for (int index = 0; index < result.Columns.Count; index++)
        {
            DatabaseClientColumn column = result.Columns[index];
            columns[index] = new SqlColumn(column.Name, index, column.Type);

            // First column wins on a duplicate name so name-based access is stable;
            // callers with duplicated projection names use ordinal access.
            ordinals.TryAdd(column.Name, index);
        }

        var rows = new SqlRow[result.Rows.Count];

        for (int index = 0; index < result.Rows.Count; index++)
        {
            rows[index] = new SqlRow(result.Rows[index], ordinals);
        }

        return new SqlResultSet(columns, rows);
    }
}
