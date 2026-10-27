using System.Collections.Generic;

using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Matches a complete fixed path, or creates its unbound parts, then applies the selected SET branch.</summary>
/// <param name="Path">The complete path to match or create.</param>
/// <param name="OnCreate">SET items from all ON CREATE actions, in source order, applied once after creation.</param>
/// <param name="OnMatch">SET items from all ON MATCH actions, in source order, applied to every complete match.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlMergeStage(GqlPathPattern Path, IReadOnlyList<GqlSetItem> OnCreate,
    IReadOnlyList<GqlSetItem> OnMatch, Location? Location = null) : GqlStage(Location)
{
    /// <summary>Renders the path and both branches as canonical GQL.</summary>
    /// <returns>The MERGE clause.</returns>
    public override string ToString() => GqlMergeRenderer.Render(this);
}
