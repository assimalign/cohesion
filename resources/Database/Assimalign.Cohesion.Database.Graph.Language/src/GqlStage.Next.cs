using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Starts a segment with the preceding projection, or one empty binding after a write.</summary>
/// <param name="Location">The source span.</param>
public sealed record GqlNextStage(Location? Location = null) : GqlStage(Location);
