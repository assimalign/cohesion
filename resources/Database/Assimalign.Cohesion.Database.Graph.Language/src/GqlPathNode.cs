namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A node element of a path pattern.</summary>
/// <param name="Pattern">The node's binding and constraints.</param>
public sealed record GqlPathNode(GqlNodePattern Pattern) : GqlPathElement;
