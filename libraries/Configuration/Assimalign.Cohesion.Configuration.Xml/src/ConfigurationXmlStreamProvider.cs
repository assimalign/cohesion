using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Xml;

using Assimalign.Cohesion.Configuration;

/// <summary>
/// Loads configuration values from an XML stream.
/// </summary>
public sealed class ConfigurationXmlStreamProvider : ConfigurationProvider
{
    private readonly Stream _stream;
    private readonly XmlDocumentDecryptor _decryptor;
    private readonly bool _leaveOpen;
    private readonly string _name;

    /// <summary>
    /// Initializes a new XML stream-backed configuration provider.
    /// </summary>
    /// <param name="stream">The XML stream to read from.</param>
    /// <param name="decryptor">The decryptor used to read encrypted XML documents.</param>
    /// <param name="leaveOpen">A value that indicates whether the stream should remain open when the provider is disposed.</param>
    public ConfigurationXmlStreamProvider(
        Stream stream,
        XmlDocumentDecryptor? decryptor = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _decryptor = decryptor ?? XmlDocumentDecryptor.Instance;
        _leaveOpen = leaveOpen;
        _name = $"{nameof(ConfigurationXmlStreamProvider)}[{RuntimeHelpers.GetHashCode(stream)}]";
    }

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    protected override Task OnLoadAsync(
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken = default)
    {
        if (_stream.CanSeek)
        {
            _stream.Position = 0;
        }

        XmlConfigurationParser.Parse(_stream, entries, _decryptor);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync(IEnumerable<IConfigurationEntry> entries)
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
