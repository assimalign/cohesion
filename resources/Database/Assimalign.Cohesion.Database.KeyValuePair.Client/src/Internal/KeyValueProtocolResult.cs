using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Internal;

/// <summary>The materialized result of one KeyValuePair protocol exchange.</summary>
internal sealed class KeyValueProtocolResult
{
    internal KeyValueProtocolResult(IReadOnlyList<KeyValueProtocolColumn> columns, IReadOnlyList<object?[]> rows, long affectedCount)
    {
        Columns = columns;
        Rows = rows;
        AffectedCount = affectedCount;
    }

    /// <summary>Gets the columns in the result.</summary>
    public IReadOnlyList<KeyValueProtocolColumn> Columns { get; }

    /// <summary>Gets the values of each materialized result record.</summary>
    public IReadOnlyList<object?[]> Rows { get; }

    /// <summary>Gets the affected count, or -1 for queries.</summary>
    public long AffectedCount { get; }
}

