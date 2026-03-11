using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;


public class Configuration : IConfiguration
{
    private readonly ConfigurationSetStrategy _setStrategy;
    private readonly IReadOnlyList<IConfigurationProvider> _providers;

    private bool _isDisposed;
    private bool _isDisposing;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public Configuration(ConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _providers = options.Providers;
        _setStrategy = options.SetStrategy;
    }

    /// <inheritdoc />
    public string? this[Path path]
    {
        get => GetConfigurationValue(path);
        set => SetConfigurationValue(path, value);
    }

    /// <inheritdoc />
    public IEnumerable<IConfigurationProvider> Providers => _providers;

    /// <inheritdoc />
    public IConfigurationEntry? GetEntry(Path path)
    {
        IConfigurationEntry? entry = default;

        for (int i = _providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = _providers[i];

            if ((entry = provider.GetEntry(path)) is not null)
            {
                return entry;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IConfigurationValue? GetValue(Path path)
    {
        IConfigurationEntry? entry = default;

        if ((entry = GetEntry(path)) is not null and IConfigurationValue value)
        {
            return value;
        }

        return null;
    }

    /// <inheritdoc />
    public IConfigurationSection? GetSection(Path path)
    {
        IConfigurationEntry? entry = default;

        if ((entry = GetEntry(path)) is not null and IConfigurationSection section)
        {
            return section;
        }

        return null;
    }


    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        return EnumeratorEntries().GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Dispose()
    {
        CheckIfDisposedOrDisposing();

        foreach(var provider in _providers)
        {
            provider.Dispose();
        }

        _isDisposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        CheckIfDisposedOrDisposing();

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        _isDisposed = true;  
    }

    private void CheckIfDisposedOrDisposing()
    {
        ObjectDisposedException.ThrowIf(_isDisposing || _isDisposing, this);
    }

    private IEnumerable<IConfigurationEntry> EnumeratorEntries()
    {
        CheckIfDisposedOrDisposing();

        for (int i = _providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = _providers[i];

            foreach (IConfigurationEntry entry in provider.GetEntries())
            {
                yield return entry;
            }
        }
    }

    private string? GetConfigurationValue(in Path path)
    {
        CheckIfDisposedOrDisposing();

        for (int i = _providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = _providers[i];

            if (provider.TryGet(path, out string? value))
            {
                return value;
            }
        }

        return null;
    }
    private void SetConfigurationValue(in Path path, string? value)
    {
        CheckIfDisposedOrDisposing();

        Key key = path[0];
        IConfigurationEntry? entry = default;

        for (int i = _providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = _providers[i];

            if (provider.TrySet(path, value))
            {
                if (_setStrategy == ConfigurationSetStrategy.Distributed)
                {

                }
            }



            entry = provider.GetEntry(key);

            if (entry is not null)
            {

            }
        }
    }
}
