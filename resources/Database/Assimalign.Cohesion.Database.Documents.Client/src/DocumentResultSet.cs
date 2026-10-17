using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents.Client;

/// <summary>A materialized document query result with column metadata and an affected count.</summary>
public sealed class DocumentResultSet : IReadOnlyList<DocumentRow>
{
    private readonly IReadOnlyList<DocumentRow> _rows;

    internal DocumentResultSet(IReadOnlyList<DocumentColumn> columns, IReadOnlyList<object?[]> rows, long affectedCount,
        bool wholeDocuments, IReadOnlyList<JsonElement> documents)
    {
        Columns = columns;
        AffectedCount = affectedCount;
        IsDocumentQuery = wholeDocuments;
        Documents = documents;
        var ordinals = new Dictionary<string, int>(columns.Count, StringComparer.Ordinal);
        foreach (DocumentColumn column in columns)
        {
            ordinals.TryAdd(column.Name, column.Ordinal);
        }
        var materialized = new DocumentRow[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            materialized[index] = new DocumentRow(rows[index], ordinals);
        }
        _rows = materialized;
    }

    /// <summary>Gets columns in projection order.</summary>
    public IReadOnlyList<DocumentColumn> Columns { get; }

    /// <summary>Gets the mutation count, or -1 for a row-returning statement.</summary>
    public long AffectedCount { get; }

    /// <summary>Gets whether the statement returns bare JSON values (SELECT *, VALUE or RAW), including an empty result.</summary>
    public bool IsDocumentQuery { get; }

    /// <summary>Gets bare JSON values for SELECT *, VALUE or RAW; empty for named projections.</summary>
    public IReadOnlyList<JsonElement> Documents { get; }

    /// <summary>Gets the number of materialized rows.</summary>
    public int Count => _rows.Count;

    /// <summary>Gets a row by index.</summary>
    /// <param name="index">The zero-based row index.</param>
    /// <returns>The materialized row.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the result.</exception>
    public DocumentRow this[int index] => _rows[index];

    /// <inheritdoc />
    public IEnumerator<DocumentRow> GetEnumerator() => _rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
