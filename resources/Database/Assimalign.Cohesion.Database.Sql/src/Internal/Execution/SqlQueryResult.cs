using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Represents the result of a SQL DML operation (INSERT, UPDATE, DELETE).
/// </summary>
internal sealed class SqlQueryResult : DatabaseResult
{
    internal SqlQueryResult(DatabaseResultStatus status, long affectedCount, IReadOnlyList<Diagnostic>? diagnostics = null)
    {
        Status = status;
        AffectedCount = affectedCount;
        Diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public override DatabaseResultStatus Status { get; }

    /// <inheritdoc />
    public override long AffectedCount { get; }

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic>? Diagnostics { get; }
}
