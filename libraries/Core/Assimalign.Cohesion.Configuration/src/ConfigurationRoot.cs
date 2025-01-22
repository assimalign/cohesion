using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

public class ConfigurationRoot : IConfigurationRoot, IDisposable
{
    private readonly IList<IConfigurationProvider> providers;
    private readonly ReaderWriterLockSlim isLocked = new();

    private bool isDisposed;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public ConfigurationRoot(ConfigurationOptions options)
    {
        ThrowHelper.ThrowIfNull(options, nameof(options));

        this.providers = options.Providers.AsReadOnly();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public object? this[KeyPath path]
    {
        get => GetConfigurationValue(path);
        set => SetConfigurationValue(path, value);
    }

    /// <summary>
    /// Returns a readonly collection of providers.
    /// </summary>
    public IEnumerable<IConfigurationProvider> Providers => this.providers;

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IConfigurationChangeToken GetChangeToken()
    {
        CheckIsDisposed();

        throw new NotImplementedException();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ConfigurationException"></exception>
    public IConfigurationProvider GetProvider(string name)
    {
        ThrowHelper.ThrowIfNull(name, nameof(name));

        for (int i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];

            if (provider.Name == name)
            {
                return provider;
            }
        }

        throw ThrowHelper.GetConfigurationException("Provider now found");
    }

    public IConfigurationSection GetSection(Key key)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IConfigurationSection> GetSections()
    {
        return GetEntriesOfType<IConfigurationSection>();
    }

    public IConfigurationValue GetValue(Key key)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IConfigurationValue> GetValues()
    {
        return GetEntriesOfType<IConfigurationValue>();
    }

    public void Reload()
    {
        foreach (var provider in providers)
        {
            provider.Reload();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            await provider.ReloadAsync(cancellationToken);
        }
    }

    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Dispose()
    {

    }

    public ValueTask DisposeAsync()
    {
        
    }

    private IEnumerable<TEntry> GetEntriesOfType<TEntry>()
    {
        foreach (var provider in providers)
        {
            foreach (var entry in provider.GetEntries())
            {
                if (entry is TEntry value)
                {
                    yield return value;
                }
            }
        }
    }

    private void CheckIsDisposed()
    {
        if (isDisposed)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(ConfigurationRoot));
        }
    }



    private object? GetConfigurationValue(KeyPath path)
    {
        Key key = default;
        IConfigurationProvider? provider = null;
        IConfigurationEntry? entry = null;

        for (int i = 0; i < providers.Count; i++)
        {
            provider = providers[i];
            key = path.GetKey(0);
            entry = provider.Get(key);

            if (entry is not null && entry is IConfigurationSection section)
            {
                return section[path.GetSubpath(1)];
            }
            if (entry is not null)

                for (int a = 0; a < path.Count; a++)
                {

                    if (entry is null)
                    {
                        break;
                    }
                }

            if (entry is not null)
            {
                break;
            }
        }

        if (entry is null || entry is not IConfigurationValue value)
        {
            return null;
        }

        return value.Value;
    }

    private void SetConfigurationValue(KeyPath path, object? value)
    {
        Key key = path.GetFirstKey();
        IConfigurationProvider? provider = null;

        for (int i = 0; i < providers.Count; i++)
        {
            provider = providers[i];

            if (provider.ContainsKey(key))
            {
                break;
            }
        }

        if (provider is null)
        {
            throw new Exception();
        }

        var compose = Compose(path, value);

        provider.Set(compose);
    }


    private IConfigurationEntry Compose(KeyPath path, object? value)
    {
        var key = path.GetKey(0);

        if (path.Count > 1)
        {
            var subpath = path.GetSubpath(1);

            return new ConfigurationSection(key)
            {
                Compose(subpath, value)
            };
        }
        else
        {
            return new ConfigurationValue(key, value);
        }
    }
}
