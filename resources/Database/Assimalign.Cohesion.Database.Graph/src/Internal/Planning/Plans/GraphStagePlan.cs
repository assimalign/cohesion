using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphStagePlan(GqlStage Stage, GraphPlan Plan)
{
    internal GraphProjectionKind ElementKind { get; init; }
}
