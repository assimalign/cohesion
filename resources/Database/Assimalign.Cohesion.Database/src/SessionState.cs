namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents the lifecycle state of a database session.
/// </summary>
public enum SessionState : byte
{
    /// <summary>The session is open and accepting operations.</summary>
    Open = 0,
    /// <summary>The session has been closed.</summary>
    Closed,
    /// <summary>The session encountered an error and is no longer usable.</summary>
    Faulted
}
