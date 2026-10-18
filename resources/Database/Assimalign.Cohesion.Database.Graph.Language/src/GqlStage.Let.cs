using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Binds expressions in assignment order for each incoming row.</summary>
/// <param name="Bindings">The expressions and their required variable aliases.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlLetStage(IReadOnlyList<GqlProjection> Bindings, Location? Location = null) : GqlStage(Location);
