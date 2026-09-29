namespace Assimalign.Cohesion.IdentityHub.Hosting.Internal;

/// <summary>
/// An audience declared through <c>AddAudience</c>, registered as a singleton.
/// </summary>
/// <param name="Value">The audience identifier.</param>
internal sealed record IdentityHubAudience(string Value);
