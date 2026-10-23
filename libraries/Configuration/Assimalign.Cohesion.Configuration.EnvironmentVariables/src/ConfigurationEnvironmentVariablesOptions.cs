namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents the options used to configure the environment variables provider.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ConfigurationEnvironmentVariablesOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly ConfigurationEnvironmentVariablesOptions _target;

        public DebugView(ConfigurationEnvironmentVariablesOptions value)
        {
            _target = value;
        }

        public string Prefix => _target.Prefix;
    }
}
