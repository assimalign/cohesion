namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents the options used to configure the environment variables provider.
/// </summary>
public sealed class ConfigurationEnvironmentVariablesOptions
{
    private string _prefix = string.Empty;

    /// <summary>
    /// Gets or sets the prefix used to filter environment variable names.
    /// </summary>
    public string Prefix
    {
        get => _prefix;
        set => _prefix = value ?? string.Empty;
    }
}
