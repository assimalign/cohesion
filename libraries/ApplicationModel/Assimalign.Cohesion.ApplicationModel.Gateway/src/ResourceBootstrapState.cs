namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Identifies one applied snapshot required before traffic admission.</summary>
/// <param name="Name">The state name.</param>
/// <param name="Key">The state slot.</param>
/// <param name="Revision">The required applied revision.</param>
/// <param name="ValueDigest">The required applied value digest.</param>
public sealed record ResourceBootstrapState(ResourceStateName Name, ResourceStateKey Key, long Revision, string ValueDigest);
