using System.Text.Json.Serialization;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Identifies an actual state snapshot required before serving traffic.</summary>
/// <param name="Name">The state contract.</param>
/// <param name="Key">The state slot.</param>
/// <param name="Revision">The required applied revision, or zero only for an exact locally frozen configured baseline.</param>
/// <param name="ValueDigest">The required committed value digest.</param>
public sealed record ResourceStateAdmissionSlot(
    [property: JsonConverter(typeof(ResourceStateNameJsonConverter))] ResourceStateName Name,
    [property: JsonConverter(typeof(ResourceStateKeyJsonConverter))] ResourceStateKey Key,
    long Revision, string ValueDigest);
