namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Reports whether the resource committed the requested state.</summary>
/// <param name="AppliedRevision">The observed committed revision.</param>
/// <param name="ErrorCode">A stable failure code when application failed.</param>
public sealed record ResourceStateDeliveryResult(long AppliedRevision, string? ErrorCode = null);
