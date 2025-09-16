using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;



public class NewConfigurationRoot : IConfigurationRoot
{

    public NewConfigurationRoot(List<IConfigurationProvider> providers)
    {
        for (int i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];

            provider.Get
        }
    }


    public string? this[Path path] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public IEnumerable<IConfigurationProvider> Providers => throw new NotImplementedException();

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public IConfigurationEntry? GetEntry(Path path)
    {
        throw new NotImplementedException();
    }

    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public class ConfigurationRoot : IConfigurationRoot
{
    private readonly ConfigurationSetStrategy _setStrategy;
    private readonly IReadOnlyList<IConfigurationProvider> _providers;

    private bool _isDisposed;
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

    /// <inheritdoc />
    public string? this[Path path]
    {
        get => GetConfigurationValue(path, this);
        set => SetConfigurationValue(path, value, this);
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

    /// <inheritdoc />
    public IChangeToken GetChangeToken()
    {
        CheckIfDisposedOrDisposing();

        throw new NotImplementedException();
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
        if (_isDisposed || _isDisposing)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(ConfigurationRoot));
        }
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

    private string? GetConfigurationValue(in Path path, ConfigurationRoot root)
    {
        Key key = path[0];
        IReadOnlyList<IConfigurationProvider> providers = (IReadOnlyList<IConfigurationProvider>)root.Providers;

        for (int i = providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = providers[i];

            if (provider.TryGet(path, out string? value))
            {
                return value;
            }
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
            entry = provider.GetEntry(key);

            if (entry is not null)
            {

            }
        }
    }
}
