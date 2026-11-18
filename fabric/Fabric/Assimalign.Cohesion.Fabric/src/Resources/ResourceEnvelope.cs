using System.Text.Json;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One resource as the ResourceManager stores and serves it: its address, identity, type, and
/// version, the metadata the ResourceManager owns, the desired state (<see cref="Spec"/>), and
/// the observed state (<see cref="Status"/>).
/// </summary>
/// <param name="Id">The resource's address. It can change when the resource moves between applications.</param>
/// <param name="Uid">The resource's identity, assigned at create and never reused.</param>
/// <param name="Type">The resource type.</param>
/// <param name="ApiVersion">The version of <paramref name="Type"/> that <paramref name="Spec"/> is written in.</param>
/// <param name="Metadata">What the ResourceManager tracks about the resource.</param>
/// <param name="Spec">The desired state, typed by the resource type's provider. The envelope keeps it as JSON so the store and the API stay type-agnostic.</param>
/// <param name="Status">The observed state, written only by the resource's reconcile; <see langword="null"/> before the first one.</param>
public sealed record ResourceEnvelope(
    ResourceId Id,
    ResourceUid Uid,
    ResourceTypeName Type,
    ApiVersion ApiVersion,
    ResourceMetadata Metadata,
    JsonElement Spec,
    ResourceStatus? Status);
