namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>Direction of a relationship relative to an incident node.</summary>
public enum GraphIncidentDirection
{
    /// <summary>Either endpoint; self-loops are returned once.</summary>
    Both,
    /// <summary>The node is the source endpoint.</summary>
    Outgoing,
    /// <summary>The node is the target endpoint.</summary>
    Incoming,
}
