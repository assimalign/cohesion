using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Applies SET items in source order for each input row.</summary>
/// <param name="Items">The ordered items.</param><param name="Location">The source span.</param>
public sealed record GqlSetStage(IReadOnlyList<GqlSetItem> Items, Location? Location = null) : GqlStage(Location);
