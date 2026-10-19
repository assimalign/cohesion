namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A relationship element, optionally quantified, of a path pattern.</summary>
/// <param name="Pattern">The relationship's binding, constraints, direction and quantifier.</param>
public sealed record GqlPathRelationship(GqlRelationshipPattern Pattern) : GqlPathElement;
