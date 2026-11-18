namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One action a provider serves on one of its types.
/// </summary>
/// <param name="Type">The resource type the action belongs to.</param>
/// <param name="Name">The action name, such as <c>rotate-signing-key</c>.</param>
/// <param name="Sensitive">Whether the action is sensitive: a role grants it only by name, never through a wildcard.</param>
public sealed record ResourceActionManifest(ResourceTypeName Type, string Name, bool Sensitive);
