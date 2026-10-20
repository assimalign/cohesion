using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphMatchClausePlan(IReadOnlyList<GraphPathPlan> Paths, GqlExpression? Predicate,
    bool Optional, IReadOnlyList<string> NewVariables, bool MatchesNothing);
