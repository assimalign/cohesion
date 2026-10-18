using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Replaces or merges a literal property map, removing keys whose values are NULL.</summary>
/// <param name="Variable">The entity variable.</param><param name="Properties">The property expressions.</param>
/// <param name="Merge">Whether to merge rather than replace.</param><param name="Location">The source span.</param>
public sealed record GqlSetPropertiesItem(string Variable, IReadOnlyDictionary<string, GqlExpression> Properties, bool Merge = false, Location? Location = null) : GqlSetItem(Variable, Location);
