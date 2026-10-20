using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphProjectionPlan(GqlExpression Expression, string Name, GraphProjectionKind Kind)
{
    internal string? BindingName { get; init; }
    internal bool MayBeComposite { get; init; }
    internal DatabaseType Type { get; init; }
    internal bool Optional { get; init; }
}
