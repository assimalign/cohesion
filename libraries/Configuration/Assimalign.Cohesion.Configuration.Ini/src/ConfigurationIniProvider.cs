using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Ini;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.FileSystem;

/// <summary>
/// Loads configuration values from an INI file within an <see cref="Assimalign.Cohesion.FileSystem.IFileSystem"/>.
/// </summary>
public sealed class ConfigurationIniProvider : FileSystemConfigurationProvider
{
    private readonly string _name;

    /// <summary>
    /// Initializes a new INI file-backed configuration provider.
    /// </summary>
    /// <param name="options">The INI file provider options.</param>
    public ConfigurationIniProvider(ConfigurationIniOptions options)
        : base(options)
    {
        _name = $"{nameof(ConfigurationIniProvider)}[{options.Path}]";
    }

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    protected override Task ReadAsync(
        Stream stream,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken = default)
    {
        return IniConfigurationParser.ParseAsync(stream, entries, cancellationToken);
    }
}
