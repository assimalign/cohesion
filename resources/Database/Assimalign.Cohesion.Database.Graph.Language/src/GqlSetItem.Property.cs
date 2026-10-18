using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Assigns a scalar property; NULL removes the key.</summary>
/// <param name="Variable">The entity variable.</param><param name="Property">The property key.</param>
/// <param name="Value">The value expression.</param><param name="Location">The source span.</param>
public sealed record GqlSetPropertyItem(string Variable, string Property, GqlExpression Value, Location? Location = null) : GqlSetItem(Variable, Location);
