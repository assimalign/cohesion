namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>One section's outcome on one instance: <c>baseline</c>, <c>applied</c>, <c>rejected</c> or <c>restartRequired</c>.</summary>
/// <param name="Path">The section path.</param>
/// <param name="State">The section state.</param>
/// <param name="Error">Why the instance refused the section, when it did.</param>
internal sealed record InstanceSectionReport(string Path, string State, string? Error);
