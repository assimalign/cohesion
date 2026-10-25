using System.Text.Json.Serialization;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Declares an initial desired state without live observations.</summary>
/// <param name="Name">The advertised state contract name.</param>
/// <param name="Key">The slot key validated by the contract.</param>
/// <param name="Desired">The portable desired value.</param>
public sealed record ResourceStateDeclaration(
    [property: JsonConverter(typeof(ResourceStateNameJsonConverter))] ResourceStateName Name,
    [property: JsonConverter(typeof(ResourceStateKeyJsonConverter))] ResourceStateKey Key,
    ResourceStateValue Desired);
