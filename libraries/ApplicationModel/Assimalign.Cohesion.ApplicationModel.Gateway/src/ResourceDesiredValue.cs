using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Contains a desired value in the runtime wire format without runtime assembly types.</summary>
/// <param name="Exists">Whether the state explicitly exists.</param>
/// <param name="Value">The desired JSON payload.</param>
public sealed record ResourceDesiredValue(bool Exists, JsonElement Value);
