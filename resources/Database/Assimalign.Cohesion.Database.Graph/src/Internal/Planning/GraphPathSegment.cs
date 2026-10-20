using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// One boundary may conjoin an outer node and the endpoint of an unquantified group.
// Repeated bodies retain their own boundaries, so each iteration checks both sides of a junction.
internal sealed record GraphPathSegment(GraphPathShape Body, GqlQuantifier? Quantifier);
