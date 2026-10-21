namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// The direction a traversal follows relationships in.
/// </summary>
public enum GraphDirection : byte
{
    /// <summary>Follow relationships from origin to target.</summary>
    Outgoing = 0,

    /// <summary>Follow relationships from target to origin.</summary>
    Incoming,

    /// <summary>Follow relationships in both directions.</summary>
    Both,
}
