using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphCallStagePlan(GqlCallStage Call, GraphPlan Inner,
    GraphCompositePlan? Composite, IReadOnlyList<string> Imports) : GraphStagePlan(Call, Inner);
