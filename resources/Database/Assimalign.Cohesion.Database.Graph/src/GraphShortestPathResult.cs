using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A selected shortest path and its actual unweighted distance.</summary>
/// <remarks>Equal-length paths select the lexicographically smallest sequence of relationship
/// identities, starting at the search's start node. Stored relationship directions are preserved.</remarks>
public sealed class GraphShortestPathResult
{
    internal GraphShortestPathResult(GraphPath path)
    {
        Path = path;
        Distance = path.Relationships.Count;
    }

    /// <summary>Gets the number of relationships in the path; zero for a visible node to itself.</summary>
    public int Distance { get; }

    /// <summary>Gets the path in start-to-target order, materialized from one statement snapshot.</summary>
    public GraphPath Path { get; }
}
