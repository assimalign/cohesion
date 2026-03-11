using System;
using System.Threading;

namespace Assimalign.Cohesion.Configuration.Internal;

internal abstract class ConfigurationEntry : IConfigurationEntry
{
    private readonly Key _key;
    private readonly Path _path;
    private readonly Lazy<ConfigurationChangeToken> _token;
    private readonly string _providerName;
    private readonly bool _isReadOnly;

    internal ConfigurationEntry(Path path, string providerName)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(providerName);

        _path = path;
        _key = path.Keys[path.Count - 1];
        _token = new Lazy<ConfigurationChangeToken>(() => new ConfigurationChangeToken(this), true);
        _providerName = providerName;
    }

    /// <inheritdoc />
    public Key Key => _key;

    /// <inheritdoc />
    public Path Path => _path;

    /// <inheritdoc />
    public string ProviderName => _providerName;

    /// <inheritdoc />
    public IChangeToken GetChangeToken()
    {
        return _token.Value;
    }

    /// <inheritdoc />
    protected void NotifyChanged()
    {
        _token.Value.Notify();
    }

    /// <inheritdoc />
    protected T NotifyChanged<T>(T? previous = default, T? current = default)
    {
        _token.Value.Notify();
        return current!;
    }
}