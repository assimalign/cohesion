using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>The materialized paths projected by a graph MATCH statement.</summary>
/// <remarks>All entities are captured from the same statement snapshot; this result owns no active operation.</remarks>
[global::System.Diagnostics.DebuggerDisplay("GraphPathsQueryResult")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.GraphPathsQueryResult.DebugView))]
public sealed class GraphPathsQueryResult : DatabaseResult
{
    private readonly IReadOnlyList<Diagnostic> _warnings;

    internal GraphPathsQueryResult(IReadOnlyList<GraphPath> paths, IReadOnlyList<Diagnostic> warnings)
    {
        Paths = paths;
        _warnings = warnings;
    }

    /// <summary>Gets the projected paths after DISTINCT, ordering and paging.</summary>
    public IReadOnlyList<GraphPath> Paths { get; }

    /// <inheritdoc />
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;

    /// <inheritdoc />
    public override long AffectedCount => -1;

    /// <inheritdoc />
    /// <remarks>
    /// An empty collection when the statement reported nothing. A MATCH that names a label or
    /// relationship type the database does not have reports a <c>CG01010</c> or <c>CG01011</c>
    /// warning here; such a name matches nothing.
    /// </remarks>
    public override IReadOnlyList<Diagnostic> Diagnostics => _warnings;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.GraphPathsQueryResult _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.GraphPathsQueryResult value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Graph.Protocol.GraphPath> Paths => _target.Paths;
        public global::Assimalign.Cohesion.Database.Execution.DatabaseResultStatus Status => _target.Status;
        public long AffectedCount => _target.AffectedCount;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Types.Diagnostic> Diagnostics => _target.Diagnostics;
    }
}
