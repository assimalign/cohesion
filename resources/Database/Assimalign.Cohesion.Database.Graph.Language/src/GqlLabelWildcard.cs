namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// The ISO/IEC 39075 wildcard label <c>%</c> (feature G074). A node matches when it carries at
/// least one label; every stored relationship has a type, so every relationship matches. Its
/// negation <c>!%</c> therefore selects unlabeled nodes and no relationship.
/// </summary>
public sealed record GqlLabelWildcard : GqlLabelExpression;
