using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Portable command data used to reconstruct an application's desired state.</summary>
/// <param name="Id">The deterministic command identity.</param>
/// <param name="Kind">The target's command kind.</param>
/// <param name="Key">The nonblank provider ownership key.</param>
/// <param name="Target">The target resource name in this model.</param>
/// <param name="Owner">The declaring application name.</param>
/// <param name="Payload">The canonical UTF-8 JSON payload, serialized as base64 bytes.</param>
/// <param name="Optional">Whether rejection may allow dependents to start.</param>
public sealed record ApplicationModelCommandDocument(
    string Id,
    string Kind,
    string Key,
    string Target,
    string Owner,
    ReadOnlyMemory<byte> Payload,
    bool Optional);
