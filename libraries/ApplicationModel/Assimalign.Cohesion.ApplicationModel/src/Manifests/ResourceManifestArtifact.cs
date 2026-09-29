using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes the build artifacts that can realize a resource.
/// </summary>
public sealed record ResourceManifestArtifact
{
    /// <summary>
    /// Gets the managed assembly that contains the resource entry point.
    /// </summary>
    public string Assembly { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the resource can be realized in-process.
    /// </summary>
    public bool Composable { get; init; }

    /// <summary>
    /// Gets the source project path, when the manifest was produced from source.
    /// </summary>
    public string? Project { get; init; }

    /// <summary>
    /// Gets the native app-host path, when the manifest was produced from source.
    /// </summary>
    [JsonPropertyName("apphost")]
    public string? AppHost { get; init; }

    /// <summary>
    /// Gets the image reference associated with the resource, when available.
    /// </summary>
    public string? Image { get; init; }
}
