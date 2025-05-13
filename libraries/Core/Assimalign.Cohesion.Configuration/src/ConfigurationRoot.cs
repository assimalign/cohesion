using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

public class ConfigurationRoot : IConfigurationRoot, IDisposable
{
    private readonly ConfigurationSetStrategy _setStrategy;
    private readonly List<IConfigurationProvider> _providers;

    private bool _disposed;
    private bool _isDisposing;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public ConfigurationRoot(ConfigurationOptions options)
    {
        ThrowHelper.ThrowIfNull(options, nameof(options));

        _providers = options.Providers;
        _setStrategy = options.SetStrategy;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public string? this[Path path]
    {
        get => GetConfigurationValue(path, this);
        set => SetConfigurationValue(path, value, this);
    }

    /// <summary>
    /// Returns a readonly collection of providers.
    /// </summary>
    public IEnumerable<IConfigurationProvider> Providers => _providers.AsReadOnly();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public IConfigurationEntry? Get(Key key)
    {
        for (int i = _providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = _providers[i];
            IConfigurationEntry? entry = provider.Get(key);

            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void Set(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry);

        if (_setStrategy == ConfigurationSetStrategy.ExistingOnly)
        {
            Key key = entry.Key;

            for (int i = _providers.Count - 1; i >= 0; i--)
            {
                IConfigurationProvider provider = _providers[i];

                if (provider.ContainsKey(key))
                {
                    provider.Set(entry);
                    return;
                }
            }
        }

        for (int i = _providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = _providers[i];

            provider.Set(entry);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void Remove(IConfigurationEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry);

        for (int i = _providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = _providers[i];

            provider.Remove(entry);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IChangeToken GetChangeToken()
    {
        CheckIfDisposedOrDisposing();

        throw new NotImplementedException();
    }

    public IConfigurationSection? GetSection(Key key) => GetEntry<IConfigurationSection>(key);
    public IEnumerable<IConfigurationSection> GetSections() => GetEntriesOf<IConfigurationSection>();
    public IConfigurationValue? GetValue(Key key) => GetEntry<IConfigurationValue>(key);
    public IEnumerable<IConfigurationValue> GetValues() => GetEntriesOf<IConfigurationValue>();
    public IEnumerator<IConfigurationEntry> GetEnumerator() => GetEntriesOf<IConfigurationEntry>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose()
    {
        CheckIfDisposedOrDisposing();

        foreach(var provider in _providers)
        {
            provider.Dispose();
        }

        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        CheckIfDisposedOrDisposing();

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        _disposed = true;  
    }

    private void CheckIfDisposedOrDisposing()
    {
        if (_disposed || _isDisposing)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(ConfigurationRoot));
        }
    }


    private TEntry? GetEntry<TEntry>(Key key)
    {
        CheckIfDisposedOrDisposing();

        IConfigurationEntry? entry = null;

        for (int i = 0; i < _providers.Count; i++)
        {
            if ((entry = _providers[i].Get(key)) is not null && entry is TEntry)
            {
                return (TEntry)entry;
            }
        }

        return default;
    }
    private IEnumerable<TEntry> GetEntriesOf<TEntry>()
    {
        CheckIfDisposedOrDisposing();

        IConfigurationProvider? provider = null;

        for (int i = 0; i < _providers.Count; i++)
        {
            provider = _providers[i];

            var entries = provider.GetEntries();

            foreach (var entry in entries)
            {
                if (entry is TEntry t)
                {
                    yield return t;
                }
            }
        }
    }

    private string? GetConfigurationValue(in Path path, ConfigurationRoot root)
    {
        Key key = path[0];
        IList<IConfigurationProvider> providers = root.Providers.ToList();

        for (int i = providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = providers[i];
            IConfigurationEntry? entry = provider.Get(key);

            if (entry is null)
            {
                continue;
            }

            if (entry.IsValue(out IConfigurationValue? value))
            {
                if (path.IsComposite)
                {
                    continue;
                }

                return value!.Value;
            }

            // This would be weird if this happened
            if (entry is not IConfigurationSection section)
            {
                continue;
            }

            Path subpath = path.Subpath(1);

            return section[subpath];
        }

        return null;
    }
    private void SetConfigurationValue(in Path path, string? value, ConfigurationRoot root)
    {
        Key key = path[0];
        IConfigurationProvider provider = default;
        IConfigurationEntry? entry = default;


        for (int i = _providers.Count - 1; i >= 0; i--)
        {
            provider = _providers[i];
            entry = provider.Get(key);

            if (entry is not null)
            {

            }
        }
    }
}
