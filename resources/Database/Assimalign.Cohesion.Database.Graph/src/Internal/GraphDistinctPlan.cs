using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphDistinctPlan(IReadOnlyList<GraphProjectionPlan> Keys);
