namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The orientation of a relationship relative to its pattern's node order.</summary>
public enum GqlPatternDirection
{
    /// <summary>The relationship points from the preceding node to the following node.</summary>
    Outgoing,
    /// <summary>The relationship points from the following node to the preceding node.</summary>
    Incoming,
    /// <summary>Either orientation is accepted.</summary>
    Undirected,
}
