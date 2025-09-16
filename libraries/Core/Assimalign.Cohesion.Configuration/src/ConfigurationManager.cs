
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
    private readonly Lock _lock;
    private readonly ConfigurationOptions _options;
    private readonly ConfigurationBuilderContext _context;
    private readonly Dictionary<string, object> _properties;

    private ConfigurationRoot _root;
    private bool _isDisposed;
    
    /// <summary>
    /// 
    /// </summary>
    public ConfigurationManager(ConfigurationOptions options)
    {
        _lock = new Lock();
        _options = ThrowHelper.ThrowIfNull(options);
        _properties = new Dictionary<string, object>();
        _root = new ConfigurationRoot(options);
        _context = new ConfigurationBuilderContext(options.Providers);
    }

    public string? this[Path path] { get => _root[path]; set => _root[path] = value; }

    public IEnumerable<IConfigurationProvider> Providers => _root.Providers;

    IConfigurationBuilder IConfigurationBuilder.AddProvider(IConfigurationProvider provider)
    {
        return AddProvider(_ => provider);
    }

    public ConfigurationManager AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> configure)
    {
        return AddProvider(context => Task.FromResult(configure.Invoke(context)));
    }

    public ConfigurationManager AddProvider(Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> configure)
    {
        ThrowHelper.ThrowIfNull(configure);

        try
        {
            IConfigurationProvider provider = configure.Invoke(_context)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();

            if (provider is null)
            {
                throw new NullReferenceException("No configuration provider was returned.");
            }

            _options.Providers.Add(provider);
        }
        catch (Exception exception) when (exception is not NullReferenceException)
        {

        }

        return this;
    }

    IConfigurationRoot IConfigurationBuilder.Build()
    {
        return this;
    }

    ValueTask<IConfigurationRoot> IConfigurationBuilder.BuildAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IConfigurationRoot>(this);
    }

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

    public IConfigurationSection? GetSection(Path path)
    {
        throw new NotImplementedException();
    }

    public IConfigurationValue? GetValue(Path path)
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}