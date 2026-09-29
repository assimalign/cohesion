using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>A materialized scalar graph result with column metadata and an affected count.</summary>
public sealed class GraphResultSet : IReadOnlyList<GraphRow>
{
    private readonly IReadOnlyList<GraphRow> _rows;

    internal GraphResultSet(IReadOnlyList<GraphColumn> columns, IReadOnlyList<object?[]> rows, long affectedCount)
    {
        Columns = columns;
        AffectedCount = affectedCount;
        var ordinals = new Dictionary<string, int>(columns.Count, StringComparer.Ordinal);
        foreach (GraphColumn column in columns)
        {
            ordinals.TryAdd(column.Name, column.Ordinal);
        }
        var materialized = new GraphRow[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            materialized[index] = new GraphRow(rows[index], ordinals);
        }
        _rows = materialized;
    }

    /// <summary>Gets columns in projection order.</summary>
    public IReadOnlyList<GraphColumn> Columns { get; }

    /// <summary>Gets the mutation count, or -1 for a row-returning statement.</summary>
    public long AffectedCount { get; }

    /// <summary>Gets the number of materialized rows.</summary>
    public int Count => _rows.Count;

    /// <summary>Gets a row by index.</summary>
    /// <param name="index">The zero-based row index.</param>
    /// <returns>The materialized row.</returns>
    public GraphRow this[int index] => _rows[index];

    /// <inheritdoc />
    public IEnumerator<GraphRow> GetEnumerator() => _rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

