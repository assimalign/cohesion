using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Boundaries refer to the enclosing shape's segments, including quantified segments.
// Retaining them prevents flattening an unquantified group from losing its predicate's scope.
internal sealed record GraphPathPrefilter(GqlExpression Predicate, int StartBoundary, int EndBoundary);
