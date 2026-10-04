using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>The materialized paths projected by a graph MATCH statement.</summary>
/// <remarks>All entities are captured from the same statement snapshot; this result owns no active operation.</remarks>
public sealed class GraphPathsQueryResult : QueryResult
{
    private readonly IReadOnlyList<Diagnostic> _warnings;

    internal GraphPathsQueryResult(IReadOnlyList<GraphPath> paths, IReadOnlyList<Diagnostic> warnings)
    {
        Paths = paths;
        _warnings = warnings;
    }

    /// <summary>Gets the matched paths in match enumeration order.</summary>
    public IReadOnlyList<GraphPath> Paths { get; }

    /// <inheritdoc />
    public override QueryResultStatus Status => QueryResultStatus.Success;

    /// <inheritdoc />
    public override long AffectedCount => -1;

    /// <inheritdoc />
    /// <remarks>
    /// <see langword="null"/> when the statement reported nothing. A MATCH that names a label or
    /// relationship type the database does not have reports a <c>COHDBG010</c> or <c>COHDBG011</c>
    /// warning here; such a name matches nothing.
    /// </remarks>
    public override IReadOnlyList<Diagnostic>? Diagnostics => _warnings.Count == 0 ? null : _warnings;
}
