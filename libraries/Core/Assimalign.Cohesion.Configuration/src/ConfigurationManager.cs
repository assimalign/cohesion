
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;
/// <summary>
/// Configuration is mutable configuration object. It is both an <see cref="IConfigurationBuilder"/> and an <see cref="IConfigurationRoot"/>.
/// As sources are added, it updates its current view of configuration. Once Build is called, configuration is frozen.
/// </summary>
public sealed class ConfigurationManager : IConfigurationManager
{
    private readonly Lock _lock;
    private readonly ConfigurationOptions _options;
    private readonly ConfigurationBuilderContext _context;
    private readonly Dictionary<string, object> _properties;

    private Configuration _root;
    private bool _isDisposed;

    /// <summary>
    /// 
    /// </summary>
    public ConfigurationManager(ConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _lock = new Lock();
        _options = options;
        _properties = new Dictionary<string, object>();
        _root = new Configuration(options);
        _context = new ConfigurationBuilderContext(options.Providers);
    }

    public string? this[Path path]
    {
        get
        {
            lock (_lock)
            {
                return _root[path];
            }
        }
        set
        {
            lock (_lock)
            {
                _root[path] = value;
            }
        }
    }

    public IEnumerable<IConfigurationProvider> Providers => _root.Providers;

    public ConfigurationManager AddProvider(Func<ConfigurationBuilderContext, IConfigurationProvider> configure)
    {
        return AddProvider(context => Task.FromResult(configure.Invoke(context)));
    }

    public ConfigurationManager AddProvider(Func<ConfigurationBuilderContext, Task<IConfigurationProvider>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(_options.LoadTimeout);

        try
        {
            CancellationToken cancellationToken = cancellationTokenSource.Token;
            ConfiguredTaskAwaitable<IConfigurationProvider>.ConfiguredTaskAwaiter awaiter = configure.Invoke(_context)
                .ConfigureAwait(false)
                .GetAwaiter();

            while (!cancellationToken.IsCancellationRequested)
            {
                if (awaiter.IsCompleted)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            IConfigurationProvider provider = awaiter.GetResult();

            lock (_lock)
            {
                _options.Providers.Add(provider);
            }
        }
        catch (Exception exception) when (exception is not NullReferenceException) { }

        return this;
    }

    public IConfigurationSection? GetSection(Path path)
    {
        if (GetEntry(path) is not IConfigurationSection section)
        {
            throw ConfigurationException.NotFound;
        }

        return section;
    }

    public IConfigurationValue? GetValue(Path path)
    {
        throw new NotImplementedException();
    }

    public IConfigurationEntry? GetEntry(Path path)
    {
        lock (_lock)
        {
            return _root.GetEntry(path);
        }
    }

    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        lock (_lock)
        {
            return _root.GetEnumerator();
        }
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
        throw new NotImplementedException();
    }

    IConfigurationManager IConfigurationManager.AddProvider(IConfigurationProvider provider)
    {
        return AddProvider(_ => provider);
    }

    IConfigurationManager IConfigurationManager.AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> provider)
    {
        return AddProvider(provider);
    }
}