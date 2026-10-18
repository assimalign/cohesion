using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Removes a property key.</summary>
/// <param name="Variable">The entity variable.</param><param name="Property">The key.</param><param name="Location">The source span.</param>
public sealed record GqlRemovePropertyItem(string Variable, string Property, Location? Location = null) : GqlRemoveItem(Variable, Location);
