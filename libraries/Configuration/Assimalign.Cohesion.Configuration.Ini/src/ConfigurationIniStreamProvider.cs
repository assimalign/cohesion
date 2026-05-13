using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Ini;

using Assimalign.Cohesion.Configuration;

/// <summary>
/// Loads configuration values from an INI stream.
/// </summary>
public sealed class ConfigurationIniStreamProvider : ConfigurationProvider
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly string _name;

    /// <summary>
    /// Initializes a new INI stream-backed configuration provider.
    /// </summary>
    /// <param name="stream">The INI stream to read from.</param>
    /// <param name="leaveOpen">A value that indicates whether the stream should remain open when the provider is disposed.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    public ConfigurationIniStreamProvider(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _leaveOpen = leaveOpen;
        _name = $"{nameof(ConfigurationIniStreamProvider)}[{RuntimeHelpers.GetHashCode(stream)}]";
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

        return IniConfigurationParser.ParseAsync(_stream, entries, cancellationToken);
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
