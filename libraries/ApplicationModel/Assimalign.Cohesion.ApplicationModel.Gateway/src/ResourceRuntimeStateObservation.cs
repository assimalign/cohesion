using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Contains a process-bound observation decoded by a gateway protocol adapter.</summary>
/// <param name="Value">The actual state value.</param>
/// <param name="Revision">The actual applied revision.</param>
/// <param name="ValueDigest">The digest committed with the value.</param>
/// <param name="ProcessStartId">The process identity established by the authenticated transport.</param>
public sealed record ResourceRuntimeStateObservation(ResourceDesiredValue Value, long Revision, string ValueDigest, Guid ProcessStartId);
