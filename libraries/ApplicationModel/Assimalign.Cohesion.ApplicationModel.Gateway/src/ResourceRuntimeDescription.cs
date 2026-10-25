using System;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Contains the catalog and process identity returned by an authenticated runtime.</summary>
/// <param name="ProcessStartId">The current process incarnation.</param>
/// <param name="Manifest">The producer-owned runtime manifest.</param>
public sealed record ResourceRuntimeDescription(Guid ProcessStartId, JsonElement Manifest);
