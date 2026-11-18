using System.Text.Json;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// A private child a provider plans or reconciles: a fabric resource the owner creates and owns,
/// named <c>{owner}.{suffix}</c>.
/// </summary>
/// <param name="Suffix">The child's suffix, one label. The ResourceManager names the child <c>{owner}.{suffix}</c>.</param>
/// <param name="Role">The child's role in the composite, such as <c>store</c> or <c>web</c>; the owner's status names a child's cause by its role.</param>
/// <param name="Type">The child's resource type, normally a fabric type such as <c>Cohesion.Workloads/workloads</c>.</param>
/// <param name="ApiVersion">The version <paramref name="Spec"/> is written in.</param>
/// <param name="Spec">The child's desired state.</param>
public sealed record ChildResource(ResourceName Suffix, string? Role, ResourceTypeName Type, ApiVersion ApiVersion, JsonElement Spec);
