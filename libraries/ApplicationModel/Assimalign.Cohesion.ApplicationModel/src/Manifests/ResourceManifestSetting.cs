namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one typed resource configuration setting.
/// </summary>
public sealed record ResourceManifestSetting
{
    /// <summary>
    /// Gets the configuration key.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Gets the manifest-provided default value.
    /// </summary>
    public string? Default { get; init; }

    /// <summary>
    /// Gets the declared value type, or <see langword="null"/> for <see cref="string"/>.
    /// </summary>
    public string? Type { get; init; }
}
