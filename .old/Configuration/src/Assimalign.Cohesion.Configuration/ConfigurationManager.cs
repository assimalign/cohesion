﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Assimalign.Cohesion.Configuration;


/// <summary>
/// Configuration is mutable configuration object. It is both an <see cref="IConfigurationBuilder"/> and an <see cref="IConfigurationRoot"/>.
/// As sources are added, it updates its current view of configuration. Once Build is called, configuration is frozen.
/// </summary>
public sealed class ConfigurationManager : IConfigurationBuilder, IConfigurationRoot, IDisposable
{
    private readonly ConfigurationSources sources;
    private readonly ConfigurationBuilderProperties properties;
    private readonly object providerLock = new();
    private readonly IList<IConfigurationProvider> providers;
    private readonly IList<IDisposable> changeTokenRegistrations;
    private ConfigurationReloadToken changeToken;

    /// <summary>
    /// Creates an empty mutable configuration object that 
    /// is both an <see cref="IConfigurationBuilder"/> and an <see cref="IConfigurationRoot"/>.
    /// </summary>
    public ConfigurationManager()
    {
        this.sources = new ConfigurationSources(this);
        this.properties = new ConfigurationBuilderProperties(this);
        this.providers = new List<IConfigurationProvider>();
        this.changeTokenRegistrations = new List<IDisposable>();
        this.changeToken = new();

        // Make sure there's some default storage since there are no default providers.
        this.AddInMemoryCollection();

        AddSource(sources[0]);
    }

    /// <inheritdoc/>
    public string this[string key]
    {
        get
        {
            lock (providerLock)
            {
                return ConfigurationRoot.GetConfiguration(providers, key);
            }
        }
        set
        {
            lock (providerLock)
            {
                ConfigurationRoot.SetConfiguration(providers, key, value);
            }
        }
    }

    /// <inheritdoc/>
    public IConfigurationSection GetSection(string key) => new ConfigurationSection(this, key);

    /// <inheritdoc/>
    public IEnumerable<IConfigurationSection> GetChildren()
    {
        lock (providerLock)
        {
            // ToList() to eagerly evaluate inside lock.
            return this.GetChildrenImplementation(null).ToList();
        }
    }

    IDictionary<string, object> IConfigurationBuilder.Properties => properties;

    IEnumerable<IConfigurationSource> IConfigurationBuilder.Sources => sources;

    IEnumerable<IConfigurationProvider> IConfigurationRoot.Providers
    {
        get
        {
            lock (providerLock)
            {
                return new List<IConfigurationProvider>(providers);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (providerLock)
        {
            DisposeRegistrationsAndProvidersUnsynchronized();
        }
    }

    IConfigurationBuilder IConfigurationBuilder.Add(IConfigurationSource source)
    {
        sources.Add(source ?? throw new ArgumentNullException(nameof(source)));
        return this;
    }

    IConfigurationRoot IConfigurationBuilder.Build() => this;

    IChangeToken IConfiguration.GetReloadToken() => changeToken;

    void IConfigurationRoot.Reload()
    {
        lock (providerLock)
        {
            foreach (var provider in providers)
            {
                provider.Load();
            }
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        var previousToken = Interlocked.Exchange(ref changeToken, new ConfigurationReloadToken());
        previousToken.OnReload();
    }

    // Don't rebuild and reload all providers in the common case when a source is simply added to the IList.
    private void AddSource(IConfigurationSource source)
    {
        lock (providerLock)
        {
            var provider = source.Build(this);
            providers.Add(provider);

            provider.Load();
            changeTokenRegistrations.Add(ChangeToken.OnChange(() => provider.GetReloadToken(), () => RaiseChanged()));
        }

        RaiseChanged();
    }

    // Something other than Add was called on IConfigurationBuilder.Sources or IConfigurationBuilder.Properties has changed.
    private void ReloadSources()
    {
        lock (providerLock)
        {
            DisposeRegistrationsAndProvidersUnsynchronized();

            changeTokenRegistrations.Clear();
            providers.Clear();

            foreach (var source in sources)
            {
                providers.Add(source.Build(this));
            }

            foreach (var p in providers)
            {
                p.Load();
                changeTokenRegistrations.Add(ChangeToken.OnChange(() => p.GetReloadToken(), () => RaiseChanged()));
            }
        }

        RaiseChanged();
    }

    private void DisposeRegistrationsAndProvidersUnsynchronized()
    {
        // dispose change token registrations
        foreach (var registration in changeTokenRegistrations)
        {
            registration.Dispose();
        }

        // dispose providers
        foreach (var provider in providers)
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    private class ConfigurationSources : IList<IConfigurationSource>
    {
        private readonly List<IConfigurationSource> _sources = new();
        private readonly ConfigurationManager _config;

        public ConfigurationSources(ConfigurationManager config)
        {
            _config = config;
        }

        public IConfigurationSource this[int index]
        {
            get => _sources[index];
            set
            {
                _sources[index] = value;
                _config.ReloadSources();
            }
        }

        public int Count => _sources.Count;

        public bool IsReadOnly => false;

        public void Add(IConfigurationSource source)
        {
            _sources.Add(source);
            _config.AddSource(source);
        }

        public void Clear()
        {
            _sources.Clear();
            _config.ReloadSources();
        }

        public bool Contains(IConfigurationSource source)
        {
            return _sources.Contains(source);
        }

        public void CopyTo(IConfigurationSource[] array, int arrayIndex)
        {
            _sources.CopyTo(array, arrayIndex);
        }

        public IEnumerator<IConfigurationSource> GetEnumerator()
        {
            return _sources.GetEnumerator();
        }

        public int IndexOf(IConfigurationSource source)
        {
            return _sources.IndexOf(source);
        }

        public void Insert(int index, IConfigurationSource source)
        {
            _sources.Insert(index, source);
            _config.ReloadSources();
        }

        public bool Remove(IConfigurationSource source)
        {
            var removed = _sources.Remove(source);
            _config.ReloadSources();
            return removed;
        }

        public void RemoveAt(int index)
        {
            _sources.RemoveAt(index);
            _config.ReloadSources();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private class ConfigurationBuilderProperties : IDictionary<string, object>
    {
        private readonly Dictionary<string, object> _properties = new();
        private readonly ConfigurationManager _config;

        public ConfigurationBuilderProperties(ConfigurationManager config)
        {
            _config = config;
        }

        public object this[string key]
        {
            get => _properties[key];
            set
            {
                _properties[key] = value;
                _config.ReloadSources();
            }
        }

        public ICollection<string> Keys => _properties.Keys;

        public ICollection<object> Values => _properties.Values;

        public int Count => _properties.Count;

        public bool IsReadOnly => false;

        public void Add(string key, object value)
        {
            _properties.Add(key, value);
            _config.ReloadSources();
        }

        public void Add(KeyValuePair<string, object> item)
        {
            ((IDictionary<string, object>)_properties).Add(item);
            _config.ReloadSources();
        }

        public void Clear()
        {
            _properties.Clear();
            _config.ReloadSources();
        }

        public bool Contains(KeyValuePair<string, object> item)
        {
            return _properties.Contains(item);
        }

        public bool ContainsKey(string key)
        {
            return _properties.ContainsKey(key);
        }

        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
        {
            ((IDictionary<string, object>)_properties).CopyTo(array, arrayIndex);
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            return _properties.GetEnumerator();
        }

        public bool Remove(string key)
        {
            var wasRemoved = _properties.Remove(key);
            _config.ReloadSources();
            return wasRemoved;
        }

        public bool Remove(KeyValuePair<string, object> item)
        {
            var wasRemoved = ((IDictionary<string, object>)_properties).Remove(item);
            _config.ReloadSources();
            return wasRemoved;
        }

        public bool TryGetValue(string key, out object value)
        {
            return _properties.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _properties.GetEnumerator();
        }
    }
}
