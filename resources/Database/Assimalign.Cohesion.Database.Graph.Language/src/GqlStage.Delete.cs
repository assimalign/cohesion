using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Deletes bound entities and removes their variables from scope.</summary>
/// <param name="Variables">The entity variables.</param>
/// <param name="Detach">Whether incident relationships are also deleted.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlDeleteStage(IReadOnlyList<string> Variables, bool Detach = false, Location? Location = null) : GqlStage(Location);
