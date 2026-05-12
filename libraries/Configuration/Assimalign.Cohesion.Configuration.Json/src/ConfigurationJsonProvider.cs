using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Json;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.FileSystem;

/// <summary>
/// Loads configuration values from a JSON file within an <see cref="Assimalign.Cohesion.FileSystem.IFileSystem"/>.
/// </summary>
public sealed class ConfigurationJsonProvider : FileSystemConfigurationProvider
{
    private readonly string _name;

    /// <summary>
    /// Initializes a new JSON file-backed configuration provider.
    /// </summary>
    /// <param name="options">The JSON file provider options.</param>
    public ConfigurationJsonProvider(ConfigurationJsonOptions options)
        : base(options)
    {
        _name = $"{nameof(ConfigurationJsonProvider)}[{options.Path}]";
    }

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    protected override Task ReadAsync(
        Stream stream,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken = default)
    {
        return JsonConfigurationParser.ParseAsync(stream, entries, cancellationToken);
    }
}
