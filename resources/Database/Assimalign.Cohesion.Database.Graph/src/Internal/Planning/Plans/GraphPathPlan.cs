using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphPathPlan(GqlPathPattern Pattern, GraphAnchor Anchor)
{
    internal GraphPathShape? Shape { get; init; }
}
