using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Carries one declarative command to a resource control plane.
/// </summary>
/// <param name="Id">The command's idempotency identifier.</param>
/// <param name="Kind">The area-defined command kind.</param>
/// <param name="Owner">The application or trust principal that owns the command key.</param>
/// <param name="Key">The stable area-defined command key.</param>
/// <param name="Payload">The area-defined command payload.</param>
public sealed record ResourceCommand(
    string Id,
    string Kind,
    string Owner,
    string Key,
    ReadOnlyMemory<byte> Payload);
