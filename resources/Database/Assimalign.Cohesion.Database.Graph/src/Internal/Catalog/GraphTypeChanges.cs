using System.Linq;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphTypeChanges
{
    // Factories copy and sort every collection ordinally. Compare the persisted fields,
    // independently of object identity, without allocating another encoded payload.
    internal static bool Same(GraphNodeType left, GraphNodeType right)
        => left.IdentifyingLabel == right.IdentifyingLabel && left.ImpliedLabels.SequenceEqual(right.ImpliedLabels)
            && SameProperties(left.Properties, right.Properties);

    internal static bool Same(GraphEdgeType left, GraphEdgeType right)
        => left.RelationshipType == right.RelationshipType && left.SourceNodeType == right.SourceNodeType
            && left.TargetNodeType == right.TargetNodeType && SameProperties(left.Properties, right.Properties);

    private static bool SameProperties(System.Collections.Generic.IReadOnlyList<GraphPropertyType> left,
        System.Collections.Generic.IReadOnlyList<GraphPropertyType> right)
        => left.Count == right.Count && left.Zip(right).All(pair => pair.First.Name == pair.Second.Name
            && pair.First.Type == pair.Second.Type && pair.First.Required == pair.Second.Required);

    internal static bool RequiresValidation(GraphNodeType? previous, GraphNodeType next)
    {
        if (previous is null) { return true; }
        if (Same(previous, next)) { return false; }
        // Exact scalar types have no subtype relation. Removing a rule or making it
        // nullable relaxes the declaration; a different scalar type requires validation.
        return next.ImpliedLabels.Except(previous.ImpliedLabels).Any() || next.Properties.Any(property =>
            !previous.Properties.Any(old => old.Name == property.Name && old.Type == property.Type
                && (!property.Required || old.Required)));
    }
}
