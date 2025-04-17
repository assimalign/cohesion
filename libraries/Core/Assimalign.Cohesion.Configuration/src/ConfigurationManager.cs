
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;


using Assimalign.Cohesion.Configuration.Internal;
using Assimalign.Cohesion.Internal;


/// <summary>
/// Configuration is mutable configuration object. It is both an <see cref="IConfigurationBuilder"/> and an <see cref="IConfigurationRoot"/>.
/// As sources are added, it updates its current view of configuration. Once Build is called, configuration is frozen.
/// </summary>
public sealed class ConfigurationManager : IConfigurationManager
{
    private readonly Lock _lock = new Lock();
    private readonly ConfigurationOptions _options;
    private readonly Dictionary<string, object> _properties;

    private ConfigurationRoot? _root;
    private bool _isDisposed;

    /// <summary>
    /// 
    /// </summary>
    public ConfigurationManager()
    {
        _options = ConfigurationOptions.Default;
        _properties = new Dictionary<string, object>();

       // _builder = new ConfigurationBuilder();
    }

    public string? this[in Path path] 
    {
        get => ConfigurationRoot.GetConfigurationValue(path, this);
        set => ConfigurationRoot.SetConfigurationValue(path, value, this);
    }

    public IEnumerable<IConfigurationProvider> Providers
    {
        get
        {
            lock (_lock)
            {
                return _options.Providers;
            }
        }
    }

    public IConfigurationBuilder AddProvider(IConfigurationProvider provider)
    {
        ThrowHelper.ThrowIfNull(provider);

        provider.Load();

        lock(_lock)
        {
            _options.Providers.Add(provider);
            _root = new ConfigurationRoot(_options);
        }

        return this;
    }
    public IConfigurationBuilder AddProvider(Func<IConfigurationContext, IConfigurationProvider> configure)
    {
        ThrowHelper.ThrowIfNull(configure);

        var context = new ConfigurationContext()
        {
            Properties = _properties,
            Providers = _options.Providers
        };

        var provider = configure.Invoke(context);

        ThrowHelper.ThrowIfNull(provider);

        provider.Load();

        lock (_lock)
        {
            _options.Providers.Add(provider);
            _root = new ConfigurationRoot(_options);
        }

        return this;
    }
    public IConfigurationBuilder AddProvider(Func<IConfigurationContext, Task<IConfigurationProvider>> configure)
    {
        ThrowHelper.ThrowIfNull(configure);

        var context = new ConfigurationContext()
        {
            Properties = _properties,
            Providers = _options.Providers
        };

        var provider = configure.Invoke(context).GetAwaiter().GetResult();

        ThrowHelper.ThrowIfNull(provider);

        provider.Load();

        lock (_lock)
        {
            _options.Providers.Add(provider);
            _root = new ConfigurationRoot(_options);
        }

        return this;
    }

    public void Set(IConfigurationEntry entry)
    {
        throw new NotImplementedException();
    }

    public void Remove(IConfigurationEntry entry)
    {
        throw new NotImplementedException();
    }

    IConfigurationRoot IConfigurationBuilder.Build()
    {
        return (_root ??= new ConfigurationRoot(_options));
    }
    Task<IConfigurationRoot> IConfigurationBuilder.BuildAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IConfigurationRoot>((this as IConfigurationBuilder).Build());
    }
    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            //DisposeRegistrationsAndProvidersUnsynchronized();
        }
    }

    private void CheckIsDisposed()
    {
        if (_isDisposed)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(ConfigurationManager));
        }
    }

}
