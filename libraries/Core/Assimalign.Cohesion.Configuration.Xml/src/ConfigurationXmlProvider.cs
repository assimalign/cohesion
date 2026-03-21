using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Xml;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.FileSystem;

/// <summary>
/// Loads configuration values from an XML file within an <see cref="Assimalign.Cohesion.FileSystem.IFileSystem"/>.
/// </summary>
public sealed class ConfigurationXmlProvider : FileSystemConfigurationProvider
{
    private readonly ConfigurationXmlOptions _options;
    private readonly string _name;

    /// <summary>
    /// Initializes a new XML file-backed configuration provider.
    /// </summary>
    /// <param name="options">The XML file provider options.</param>
    public ConfigurationXmlProvider(ConfigurationXmlOptions options)
        : base(options)
    {
        _options = options;
        _name = $"{nameof(ConfigurationXmlProvider)}[{options.Path}]";
    }

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    protected override Task ReadAsync(
        Stream stream,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken = default)
    {
        XmlConfigurationParser.Parse(stream, entries, _options.Decryptor);
        return Task.CompletedTask;
    }
}
