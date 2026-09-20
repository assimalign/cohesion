using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>The materialized paths projected by a graph MATCH statement.</summary>
/// <remarks>All entities are captured from the same statement snapshot; this result owns no active operation.</remarks>
public sealed class GraphPathsQueryResult : QueryResult
{
    internal GraphPathsQueryResult(IReadOnlyList<GraphPath> paths) => Paths = paths;

    /// <summary>Gets the matched paths in match enumeration order.</summary>
    public IReadOnlyList<GraphPath> Paths { get; }

    /// <inheritdoc />
    public override QueryResultStatus Status => QueryResultStatus.Success;

    /// <inheritdoc />
    public override long AffectedCount => -1;

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;
}
