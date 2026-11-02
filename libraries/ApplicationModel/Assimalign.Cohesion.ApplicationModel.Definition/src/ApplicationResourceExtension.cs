using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One platform extension of a resource as manifest data: configuration of how one platform deploys
/// the resource, such as a Kubernetes autoscaler, that only that platform applies.
/// </summary>
/// <remarks>
/// A platform package's verb, such as a Kubernetes package's <c>ConfigureHpa</c>, records its
/// options as JSON through <see cref="IApplicationResourceDefinition.WithExtension"/>. A gateway hands
/// a resource's extensions only to an infrastructure provider of the same platform, which reads the
/// value with its own JSON contract; a provider of any other platform never sees them.
/// </remarks>
/// <param name="Platform">The platform the extension applies on: its apply condition.</param>
/// <param name="Name">The extension's name, unique among the resource's extensions for the platform.</param>
/// <param name="Value">The extension's options, as JSON only that platform's provider reads.</param>
public sealed record ApplicationResourceExtension(
    PlatformName Platform,
    ExtensionName Name,
    JsonElement Value);
