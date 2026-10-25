namespace Assimalign.Cohesion.Web.Cors;

/// <summary>Reports a versioned policy publication outcome without a hosting dependency.</summary>
public enum CorsPolicyUpdateStatus
{
    /// <summary>The policies and version were published together.</summary>
    Applied,
    /// <summary>The same version and digest were already published.</summary>
    AlreadyCurrent,
    /// <summary>A newer version is already active.</summary>
    Stale,
    /// <summary>The authority or digest conflicts with active state.</summary>
    Conflict
}
