using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Describes state actually applied by one resource process.</summary>
/// <param name="Value">The current feature state.</param>
/// <param name="Revision">Its applied revision; zero denotes unmanaged startup state.</param>
/// <param name="ValueDigest">The digest committed with the state.</param>
/// <param name="ProcessStartId">The process incarnation; supplied by the control plane.</param>
public sealed record ResourceStateObservation(
    ResourceStateValue Value, long Revision, string ValueDigest, Guid ProcessStartId);
