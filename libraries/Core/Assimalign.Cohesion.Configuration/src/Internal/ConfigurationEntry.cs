using System;
using System.Threading;

namespace Assimalign.Cohesion.Configuration.Internal;

internal abstract class ConfigurationEntry : IConfigurationEntry
{
    private readonly Key _key;
    private readonly Path _path;
    private readonly Lazy<ConfigurationChangeToken> _token;
    private readonly string _providerName;
    private readonly ConfigurationSection? _parent;

    internal ConfigurationEntry(Path path, string providerName, ConfigurationSection? parent = null)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(providerName);

        _path = path;
        _key = path.Keys[path.Count - 1];
        _token = new Lazy<ConfigurationChangeToken>(() => new ConfigurationChangeToken(), true);
        _providerName = providerName;
        _parent = parent;
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
        NotifyLocalChanged();
        _parent?.NotifyChanged();
    }

    internal void NotifyLocalChanged()
    {
        if (_token.IsValueCreated)
        {
            _token.Value.Notify();
        }
    }
}
