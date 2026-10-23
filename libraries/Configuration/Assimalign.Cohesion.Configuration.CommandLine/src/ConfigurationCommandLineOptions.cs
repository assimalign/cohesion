using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.CommandLine;

/// <summary>
/// Represents the options used to configure the command-line provider.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ConfigurationCommandLineOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class ConfigurationCommandLineOptions
{
    private IEnumerable<string> _args = [];

    /// <summary>
    /// Gets or sets the command-line arguments.
    /// </summary>
    public IEnumerable<string> Args
    {
        get => _args;
        set => _args = value ?? [];
    }

    /// <summary>
    /// Gets or sets the switch mappings.
    /// </summary>
    public IDictionary<string, string>? SwitchMappings { get; set; }

    private sealed class DebugView
    {
        private readonly ConfigurationCommandLineOptions _target;

        public DebugView(ConfigurationCommandLineOptions value)
        {
            _target = value;
        }

        public IEnumerable<string> Args => _target.Args;
        public IDictionary<string, string>? SwitchMappings => _target.SwitchMappings;
    }
}
