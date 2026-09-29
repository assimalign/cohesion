namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Immutable, payload-free observation of one application's resource command.</summary>
/// <param name="Target">The target resource name in the declaring model.</param>
/// <param name="Id">The deterministic command identifier.</param>
/// <param name="Kind">The accepted area command kind.</param>
/// <param name="Owner">The declaring application.</param>
/// <param name="Key">The nonblank provider-scoped ownership conflict key.</param>
/// <param name="Status">The target's observed outcome.</param>
/// <param name="Detail">The target's explanation of the outcome.</param>
public sealed record ResourceCommandObservation(
    string Target,
    string Id,
    string Kind,
    string Owner,
    string Key,
    ResourceCommandStatus Status,
    string Detail);
