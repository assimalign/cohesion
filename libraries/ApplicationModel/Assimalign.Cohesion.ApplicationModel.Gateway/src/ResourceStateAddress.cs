using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Identifies a logical resource's managed state slot.</summary>
/// <param name="Application">The declaring application.</param>
/// <param name="Resource">The logical resource.</param>
/// <param name="State">The producer-declared state contract.</param>
/// <param name="Key">The contract-validated state key.</param>
public sealed record ResourceStateAddress(
    [property: JsonConverter(typeof(ApplicationNameJsonConverter))] ApplicationName Application,
    [property: JsonConverter(typeof(ResourceNameJsonConverter))] ResourceName Resource,
    [property: JsonConverter(typeof(ResourceStateNameJsonConverter))] ResourceStateName State,
    [property: JsonConverter(typeof(ResourceStateKeyJsonConverter))] ResourceStateKey Key);
