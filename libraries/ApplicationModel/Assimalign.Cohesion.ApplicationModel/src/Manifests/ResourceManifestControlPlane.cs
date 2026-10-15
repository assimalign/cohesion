namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Locates the resource area's default control plane.
/// </summary>
public sealed record ResourceManifestControlPlane
{
    /// <summary>
    /// Gets the logical endpoint that serves the control plane.
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets the path prefix under which the control plane is served.
    /// </summary>
    // Deviates from the repo value-object rule per the owner's Database freeze (#1168): the manifest DTO keeps string; validation uses ResourceHttpPath.IsValid. Switch with #1470.
    public string Path { get; init; } = string.Empty;
}
