using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents the result of a SQL DML operation (INSERT, UPDATE, DELETE).
/// </summary>
internal sealed class SqlQueryResult : QueryResult
{
    internal SqlQueryResult(QueryResultStatus status, long affectedCount, IReadOnlyList<Diagnostic>? diagnostics = null)
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
