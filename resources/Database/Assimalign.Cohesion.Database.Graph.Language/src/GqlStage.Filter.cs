using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Keeps incoming rows whose predicate is TRUE.</summary>
/// <param name="Predicate">The filter expression.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlFilterStage(GqlExpression Predicate, Location? Location = null) : GqlStage(Location);
