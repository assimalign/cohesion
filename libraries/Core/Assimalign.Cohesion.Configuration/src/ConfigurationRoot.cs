using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;
using System.Linq;

public class ConfigurationRoot : IConfigurationRoot, IDisposable
{
    private readonly ConfigurationSetStrategy setStrategy;
    private readonly IList<IConfigurationProvider> providers;

    private bool isDisposed;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public ConfigurationRoot(ConfigurationOptions options)
    {
        ThrowHelper.ThrowIfNull(options, nameof(options));

        this.providers = options.Providers.AsReadOnly();
        this.setStrategy = options.SetStrategy;
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

    public IConfigurationSection? GetSection(Key key) => GetEntry<IConfigurationSection>(key);
    public IEnumerable<IConfigurationSection> GetSections() => GetEntriesOf<IConfigurationSection>();
    public IConfigurationValue? GetValue(Key key) => GetEntry<IConfigurationValue>(key);
    public IEnumerable<IConfigurationValue> GetValues() => GetEntriesOf<IConfigurationValue>();
    public IEnumerator<IConfigurationEntry> GetEnumerator() => GetEntriesOf<IConfigurationEntry>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        CheckIsDisposed();

        foreach (var provider in providers)
        {
            await provider.DisposeAsync();
        }

        isDisposed = true;  
    }

    private void CheckIsDisposed()
    {
        if (isDisposed)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(ConfigurationRoot));
        }
    }


    private TEntry? GetEntry<TEntry>(Key key)
    {
        CheckIsDisposed();

        IConfigurationEntry? entry = null;

        for (int i = 0; i < providers.Count; i++)
        {
            if ((entry = providers[i].Get(key)) is not null && entry is TEntry)
            {
                return (TEntry)entry;
            }
        }

        return default;
    }
    private IEnumerable<TEntry> GetEntriesOf<TEntry>()
    {
        CheckIsDisposed();

        IConfigurationProvider? provider = null;

        for (int i = 0; i < providers.Count; i++)
        {
            provider = providers[i];

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



    private object? GetConfigurationValue(KeyPath path)
    {
        if (path.IsComposite)
        {
            return GetValue(path[0])?.Value;
        }

        IConfiguration? configuration = this;
        IConfigurationValue? value = null;

        for (int i = 0; i < path.Count - 1; i++)
        {
            if (configuration is not null)
            {
                configuration = configuration.GetSection(path[i]);
            }
            else
            {
                break;
            }
        }

        if (configuration is not null)
        {
            value = configuration.GetValue(path[path.Count]);
        }

        return value?.Value;
    }

    private void SetConfigurationValue(KeyPath path, object? value)
    {
        // Compose an entry from the path and value
        var entry = Compose(path, value);
        var key = path[0];

        for (int i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];

            if (provider.ContainsKey(key))
            {
                var existing = provider.Get(key);

                if (entry is IConfigurationValue)
                {
                    provider.Set(entry);
                }
                if (existing is IConfigurationSection section1 && entry is IConfigurationSection section2)
                {
                    var merge = section1.Merge(section2);

                    provider.Set(merge);
                }
            }
            else if (setStrategy == ConfigurationSetStrategy.Distributed)
            {
                provider.Set(entry);
            }
        }

        //if (entry is IConfigurationSection section)
        //{
        //    bool existing = false;

        //    for (int i = 0; i < entries.Count; i++)
        //    {
        //        var item = entries[i];

        //        if (!(existing = item.Key == entry.Key))
        //        {
        //            continue;
        //        }

        //        // Switch to composite structure
        //        if (item is IConfigurationValue)
        //        {
        //            entries.Remove(item);
        //            entries.Add(entry);
        //        }

        //        if (item is IConfigurationSection)
        //        {
        //            // Copy items to existing 
        //            foreach (var child in section)
        //            {
        //                ((IConfigurationSection)item).Add(child);
        //            }
        //        }

        //        break;
        //    }

        //    // if no existing value, then simply add
        //    if (!existing)
        //    {
        //        entries.Add(entry);
        //    }
        //}
        //else if (entry is IConfigurationValue value)
        //{
        //    // Just add or override what is existing
        //    entries.Add(value);
        //}
        //else
        //{
        //    // Invalid entry
        //    throw new Exception();
        //}
        //Key key = path[0];

        //IConfigurationProvider? provider = null;

        //for (int i = 0; i < providers.Count; i++)
        //{
        //    provider = providers[i];

        //    if (provider.ContainsKey(key))
        //    {
        //        entry = provider.Get(key);
        //        break;
        //    }
        //}

        //if (provider is null)
        //{
        //    throw new Exception();
        //}

        //IConfigurationEntry entry = provider.Get(key);

        //if (entry is null)
        //{
        //    throw new Exception();
        //}



        //for (int k = 1; k < path.Count; k++)
        //{
        //    var subpath = path.Subpath(k);

        //    if (entry is IConfigurationSection section)
        //    {

        //        entry = section[subpath];
        //    }

        //    if ((entry is null || entry is IConfigurationValue) && path.Count)
        //    {

        //    }

        //}


        //for (int i = 0; i < providers.Count; i++)
        //{
        //    provider = providers[i];

        //    if (provider.ContainsKey(key))
        //    {
        //        break;
        //    }
        //}

        //if (provider is null)
        //{
        //    throw new Exception();
        //}

        //var compose = Compose(path, value);

        //provider.Set(compose);
    }

    public static IConfigurationEntry Compose(KeyPath path, object? value)
    {
        if (path.IsComposite)
        {
            return new ConfigurationSection(path[0])
            {
                Compose(path.Subpath(1), value)
            };
        }
        else
        {
            return new ConfigurationValue(path[0], value);
        }
    }
}
