using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphMutationResult")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphMutationResult.DebugView))]
internal sealed class GraphMutationResult : DatabaseResult
{
    private readonly long _count;
    private readonly IReadOnlyList<Diagnostic> _warnings;

    /// <summary>Initializes a new instance of the <see cref="GraphMutationResult"/> class.</summary>
    /// <param name="count">The number of graph entities the mutation affected.</param>
    /// <param name="warnings">The statement's warnings; a statement without a projection reports them here.</param>
    public GraphMutationResult(long count, IReadOnlyList<Diagnostic> warnings)
    {
        _count = count;
        _warnings = warnings;
    }

    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => _count;
    public override IReadOnlyList<Diagnostic> Diagnostics => _warnings;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphMutationResult _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphMutationResult value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Execution.DatabaseResultStatus Status => _target.Status;
        public long AffectedCount => _target.AffectedCount;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Types.Diagnostic> Diagnostics => _target.Diagnostics;
    }
}
