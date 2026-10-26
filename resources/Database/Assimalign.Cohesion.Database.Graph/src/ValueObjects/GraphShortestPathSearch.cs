using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Specifies an unweighted shortest search between two database-local node identities.</summary>
/// <param name="Start">The starting node.</param>
/// <param name="Target">The destination node.</param>
/// <param name="Direction">The direction to follow; the default is outgoing.</param>
/// <param name="RelationshipType">An ordinal, case-sensitive type filter, or null for every type.</param>
/// <remarks>Search has no hop cap. The statement memory and physical-work budgets bound it;
/// exhausting either budget fails rather than returning an incomplete no-path result.</remarks>
public readonly record struct GraphShortestPathSearch(
    GraphNodeId Start,
    GraphNodeId Target,
    GraphDirection Direction = GraphDirection.Outgoing,
    string? RelationshipType = null)
{
    /// <summary>Gets optional lower statement limits; omitted limits use the engine ceilings.</summary>
    /// <remarks>Defaults are 256 MiB of accounted retained memory and 1,000,000 physical work units.</remarks>
    public GraphQueryLimits? Limits { get; init; }
}
