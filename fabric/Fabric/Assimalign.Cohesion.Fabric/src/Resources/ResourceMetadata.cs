using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What the ResourceManager tracks about a resource, beside its spec and status.
/// </summary>
/// <param name="Generation">Incremented on every spec change. <see cref="ResourceStatus.ObservedGeneration"/> below it means the reconciler has not caught up.</param>
/// <param name="ETag">The optimistic-concurrency token of the stored resource.</param>
/// <param name="Owner">The uid of the application, or of the composite that owns this resource as a private child.</param>
/// <param name="Visibility">Whether the resource is addressable by the application's principals or only through its owner.</param>
/// <param name="ManagedFields">Which manager owns which spec fields, for apply semantics.</param>
/// <param name="Finalizers">Providers that must release the resource before it is deleted.</param>
/// <param name="Labels">Free-form labels for views. A label never grants access and is never inherited.</param>
public sealed record ResourceMetadata(
    long Generation,
    string? ETag,
    ResourceUid? Owner,
    ResourceVisibility Visibility,
    IReadOnlyList<ManagedFieldsEntry> ManagedFields,
    IReadOnlyList<string> Finalizers,
    IReadOnlyDictionary<string, string> Labels);
