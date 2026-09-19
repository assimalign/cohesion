using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>The materialized result of one Sql protocol exchange.</summary>
public sealed class DatabaseClientResult
{
    internal DatabaseClientResult(IReadOnlyList<DatabaseClientColumn> columns, IReadOnlyList<object?[]> rows, long affectedCount)
    {
        Columns = columns;
        Rows = rows;
        AffectedCount = affectedCount;
    }

    /// <summary>Gets the columns in the result.</summary>
    public IReadOnlyList<DatabaseClientColumn> Columns { get; }

    /// <summary>Gets the values of each materialized result record.</summary>
    public IReadOnlyList<object?[]> Rows { get; }

    /// <summary>Gets the affected count, or -1 for queries.</summary>
    public long AffectedCount { get; }
}

