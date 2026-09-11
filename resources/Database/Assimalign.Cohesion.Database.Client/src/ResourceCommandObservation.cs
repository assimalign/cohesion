namespace Assimalign.Cohesion.Database.Client;

/// <summary>Reports the resource's observed result for a declarative mutation.</summary>
/// <param name="Status">The observed status, such as Applied, Deleted, or Rejected.</param>
/// <param name="Detail">The provider's actionable refusal or status detail.</param>
public sealed record ResourceCommandObservation(string Status, string? Detail = null);
