using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed record GraphGroupingPlan(IReadOnlyList<GqlExpression> Keys, IReadOnlyList<GqlAggregateExpression> Aggregates, GraphGroupingScope Scope);
