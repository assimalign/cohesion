using System.Text.Json;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The read-only view of one resource that a provider validates, plans, and reconciles.
/// </summary>
/// <param name="Id">The resource's address.</param>
/// <param name="Uid">The resource's identity. Platform objects are labeled and selected by it.</param>
/// <param name="Type">The resource type.</param>
/// <param name="ApiVersion">The version <paramref name="Spec"/> is written in.</param>
/// <param name="Generation">The spec generation this view carries.</param>
/// <param name="Owner">The owning application's or composite's uid.</param>
/// <param name="Spec">The desired state, with every <c>$ref</c> already resolved when the view is handed to a reconcile, and unresolved when it is handed to <see cref="IResourceProvider.Validate"/> or <see cref="IResourceProvider.Plan"/>.</param>
public sealed record ResourceView(
    ResourceId Id,
    ResourceUid Uid,
    ResourceTypeName Type,
    ApiVersion ApiVersion,
    long Generation,
    ResourceUid? Owner,
    JsonElement Spec);
