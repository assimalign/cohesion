using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Only composite stages pay for a nested plan reference.
internal sealed record GraphCompositeStagePlan : GraphStagePlan
{
    internal GraphCompositeStagePlan(GqlCompositeStage stage, GraphPlan plan, GraphCompositePlan composite) : base(stage, plan)
    { Composite = composite; }

    internal GraphCompositePlan Composite { get; }
}
