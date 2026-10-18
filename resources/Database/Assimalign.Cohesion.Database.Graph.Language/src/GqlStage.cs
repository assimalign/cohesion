using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A source-ordered operation on a graph binding table.</summary>
/// <param name="Location">The source span.</param>
public abstract record GqlStage(Location? Location = null);
