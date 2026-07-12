using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// A concrete, model-agnostic result for statements that do not return rows
/// (DML, DDL, transaction control): a status, an affected-record count, and any
/// diagnostics produced along the way.
/// </summary>
public sealed class QueryStatementResult : QueryResult
{
    private readonly IReadOnlyList<Diagnostic>? _diagnostics;

    /// <summary>
    /// Initializes a new statement result.
    /// </summary>
    /// <param name="status">The outcome status.</param>
    /// <param name="affectedCount">The number of records affected, or -1 when unknown.</param>
    /// <param name="diagnostics">Diagnostics produced during parsing or execution.</param>
    public QueryStatementResult(QueryResultStatus status, long affectedCount = -1, IReadOnlyList<Diagnostic>? diagnostics = null)
    {
        Status = status;
        AffectedCount = affectedCount;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public override QueryResultStatus Status { get; }

    /// <inheritdoc />
    public override long AffectedCount { get; }

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic>? Diagnostics => _diagnostics;

    /// <summary>
    /// A reusable successful result with no affected-count information.
    /// </summary>
    public static QueryStatementResult Success { get; } = new(QueryResultStatus.Success);
}
