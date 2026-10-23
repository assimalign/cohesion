using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[global::System.Diagnostics.DebuggerDisplay("GraphStagePlan")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphStagePlan.DebugView))]
internal sealed record GraphStagePlan(GqlStage Stage, GraphPlan Plan)
{
    internal GraphProjectionKind ElementKind { get; init; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphStagePlan _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphStagePlan value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlStage Stage => _target.Stage;
        public global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlan Plan => _target.Plan;
    }
}
