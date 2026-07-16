using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

/// <summary>
/// The result of a non-row-returning key-value command (DELETE): a status and
/// the number of entries affected.
/// </summary>
internal sealed class KeyValueQueryResult : QueryResult
{
    internal KeyValueQueryResult(QueryResultStatus status, long affectedCount, IReadOnlyList<Diagnostic>? diagnostics = null)
    {
        Status = status;
        AffectedCount = affectedCount;
        Diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public override QueryResultStatus Status { get; }

    /// <inheritdoc />
    public override long AffectedCount { get; }

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic>? Diagnostics { get; }
}
