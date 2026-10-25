namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Reports one configuration section's outcome on this instance.</summary>
/// <param name="Path">The section path.</param>
/// <param name="State">Which value is in force.</param>
/// <param name="Error">Why the section refused the record's value, when <paramref name="State"/> is <see cref="ResourceConfigurationState.Rejected"/>.</param>
public sealed record ResourceConfigurationSectionStatus(
    ResourceConfigurationPath Path,
    ResourceConfigurationState State,
    string? Error = null);
