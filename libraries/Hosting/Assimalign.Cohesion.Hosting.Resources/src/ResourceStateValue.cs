using System.Text.Json;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Represents an explicit desired or observed state value.</summary>
/// <param name="Exists">Whether the slot has a value; absence is a separate operation.</param>
/// <param name="Value">The JSON value, copied at dispatch boundaries.</param>
public sealed record ResourceStateValue(bool Exists, JsonElement Value);
