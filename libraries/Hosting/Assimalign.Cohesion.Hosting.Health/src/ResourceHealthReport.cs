using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Health;

/// <summary>
/// Represents an aggregated resource health result and its named contributions.
/// </summary>
/// <param name="Status">The least healthy contribution status, or healthy when empty.</param>
/// <param name="Contributions">The contribution snapshot keyed by contributor name.</param>
public sealed record ResourceHealthReport(
    HealthStatus Status,
    IReadOnlyDictionary<string, HealthContribution> Contributions);
