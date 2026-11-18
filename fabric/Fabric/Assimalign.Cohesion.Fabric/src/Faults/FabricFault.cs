namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One cause of a failed or stalled reconcile, in Cohesion's platform-neutral terms, with the
/// platform's own words kept beside it.
/// </summary>
/// <param name="Code">The platform-neutral cause. Platforms add reasons, never codes.</param>
/// <param name="Disposition">Whether the reconciler retries or fails the operation at once.</param>
/// <param name="Message">A Cohesion sentence that names the resource.</param>
/// <param name="Platform">The platform's detail, verbatim: never dropped and never rewritten as the fault climbs ownership edges.</param>
public sealed record FabricFault(
    FaultCode Code,
    FaultDisposition Disposition,
    string Message,
    PlatformDetail Platform);
