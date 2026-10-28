using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Ordinary rows, closures and iterators carry no override, captured-row or row-scope fields.
// This leaf and its iterator exist only during sequence-dependent insertion.
internal sealed class SqlSequenceInsertValues : List<object?[]>
{
    private List<IReadOnlyDictionary<ulong, long>?>? _captures;

    internal SqlSequenceInsertValues(int capacity, SqlIdentityOverride overriding) : base(capacity)
    { Overriding = overriding; }

    internal SqlIdentityOverride Overriding { get; }

    internal void Add(object?[] row, IReadOnlyDictionary<ulong, long>? captured)
    {
        if (captured is not null && _captures is null)
        {
            _captures = new(Capacity);
            while (_captures.Count < Count) { _captures.Add(null); }
        }
        _captures?.Add(captured);
        Add(row);
    }

    internal static IEnumerable<object?[]> ScopeRows(IReadOnlyList<object?[]> source, IEnumerable<object?[]> rows, SqlCatalogTable table)
    {
        if (SqlSequenceExecution.Current is not { } execution) { return rows; }
        bool scoped = source is SqlSequenceInsertValues { _captures: not null };
        for (int i = 0; i < table.Columns.Count && !scoped; i++)
        {
            var calls = table.Columns[i].DefaultFunctionCalls;
            for (int j = 0; j < calls.Count; j++)
            { if (calls[j].StartsWith("S:", System.StringComparison.Ordinal)) { scoped = true; break; } }
        }
        return scoped ? ScopedRows(execution, source as SqlSequenceInsertValues, rows) : rows;
    }

    private static IEnumerable<object?[]> ScopedRows(SqlSequenceExecution execution, SqlSequenceInsertValues? source,
        IEnumerable<object?[]> rows)
    {
        using var enumerator = rows.GetEnumerator();
        int index = 0;
        while (true)
        {
            using var scope = execution.BeginRow(source?._captures is { } captures && index < captures.Count ? captures[index] : null);
            if (!enumerator.MoveNext()) { yield break; }
            yield return enumerator.Current;
            index++;
        }
    }

}
