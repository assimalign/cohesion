namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Reports the result of applying actual feature state.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Revision">The observed applied revision.</param>
/// <param name="ErrorCode">A stable failure code without secret data.</param>
public sealed record ResourceStateApplyResult(ResourceStateApplyStatus Status, long Revision, string? ErrorCode = null);

/// <summary>Identifies a local state application outcome.</summary>
public enum ResourceStateApplyStatus
{
    /// <summary>The actual state and revision were committed.</summary>
    Applied,
    /// <summary>The same revision and digest were already committed.</summary>
    AlreadyCurrent,
    /// <summary>A newer revision has already been applied.</summary>
    Stale,
    /// <summary>The revision, incarnation or content conflicts.</summary>
    Conflict,
    /// <summary>The input or target was rejected.</summary>
    Rejected
}
