using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents a mutable configuration root that loads providers as they are added.
/// </summary>
public sealed class ConfigurationManager : IConfigurationManager
{
    private readonly Lock _lock;
    private readonly SemaphoreSlim _updateLock;
    private readonly ConfigurationOptions _options;
    private readonly ConfigurationBuilderContext _context;
    private readonly Configuration _root;

    private bool _isDisposed;

    /// <summary>
    /// Creates a manager with the default configuration options.
    /// </summary>
    public ConfigurationManager() : this(ConfigurationOptions.Default)
    {
    }

    /// <summary>
    /// Creates a manager and loads any preconfigured providers.
    /// </summary>
    /// <param name="options">The manager options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public ConfigurationManager(ConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _lock = new Lock();
        _updateLock = new SemaphoreSlim(1, 1);
        _options = options.CreateSnapshot([]);
        _root = new Configuration(_options);
        _context = new ConfigurationBuilderContext(_options.LoadTimeout, _options.Providers);

        foreach (IConfigurationProvider provider in options.Providers)
        {
            AddProvider(provider);
        }
    }

    /// <inheritdoc />
    public string? this[Path path]
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return _root[path];
            }
        }
        set
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                _root[path] = value;
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<IConfigurationProvider> Providers
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return [.. _root.Providers];
            }
        }
    }

    /// <summary>
    /// Loads and adds the specified provider to the current configuration view.
    /// </summary>
    /// <param name="provider">The provider to add.</param>
    /// <returns>The current manager.</returns>
    public ConfigurationManager AddProvider(IConfigurationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return AddProvider(_ => provider);
    }

    /// <summary>
    /// Creates, loads, and adds a provider to the current configuration view.
    /// </summary>
    /// <param name="configure">The provider factory.</param>
    /// <returns>The current manager.</returns>
    public ConfigurationManager AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return AddProviderAsync(
                context => Task.FromResult(configure.Invoke(context)))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Asynchronously creates, loads, and adds a provider to the current configuration view.
    /// </summary>
    /// <param name="configure">The asynchronous provider factory.</param>
    /// <param name="cancellationToken">The cancellation token for the registration operation.</param>
    /// <returns>A task that resolves to the current manager.</returns>
    public async ValueTask<ConfigurationManager> AddProviderAsync(
        Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();

            using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            ApplyLoadTimeout(cancellationTokenSource);

            IConfigurationProvider provider = await CreateProviderAsync(
                configure,
                cancellationTokenSource.Token,
                cancellationToken).ConfigureAwait(false);

            try
            {
                InvalidOperationException.ThrowIf(
                    _context.HasProvider(provider.Name),
                    $"The configuration provider: '{provider.Name}' has already been added.");

                await LoadProviderAsync(provider, cancellationTokenSource.Token, cancellationToken).ConfigureAwait(false);

                lock (_lock)
                {
                    ThrowIfDisposed();
                    _options.Providers.Add(provider);
                }

                _context.AddProvider(provider);

                return this;
            }
            catch
            {
                await DisposeProviderOnFailureAsync(provider).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <summary>
    /// Gets the section at the specified path.
    /// </summary>
    /// <param name="path">The section path.</param>
    /// <returns>The matching section.</returns>
    /// <exception cref="ConfigurationException">Thrown when the path does not resolve to a section.</exception>
    public IConfigurationSection? GetSection(Path path)
    {
        if (GetEntry(path) is not IConfigurationSection section)
        {
            throw ConfigurationException.NotFound;
        }

        return section;
    }

    /// <summary>
    /// Gets the value at the specified path.
    /// </summary>
    /// <param name="path">The value path.</param>
    /// <returns>The matching value, or <see langword="null"/>.</returns>
    public IConfigurationValue? GetValue(Path path)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _root.GetValue(path);
        }
    }

    /// <inheritdoc />
    public IConfigurationEntry? GetEntry(Path path)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _root.GetEntry(path);
        }
    }

    /// <inheritdoc />
    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return new List<IConfigurationEntry>(_root).GetEnumerator();
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _updateLock.Wait();

        try
        {
            lock (_lock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
            }

            _root.Dispose();
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _updateLock.WaitAsync().ConfigureAwait(false);

        try
        {
            lock (_lock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
            }

            await _root.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    IConfigurationManager IConfigurationManager.AddProvider(IConfigurationProvider provider)
    {
        return AddProvider(provider);
    }

    IConfigurationManager IConfigurationManager.AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> provider)
    {
        return AddProvider(provider);
    }

    async ValueTask<IConfigurationManager> IConfigurationManager.AddProviderAsync(
        Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> provider,
        CancellationToken cancellationToken)
    {
        return await AddProviderAsync(provider, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private void ApplyLoadTimeout(CancellationTokenSource cancellationTokenSource)
    {
        if (_options.LoadTimeout > TimeSpan.Zero)
        {
            cancellationTokenSource.CancelAfter(_options.LoadTimeout);
        }
    }

    private async Task<IConfigurationProvider> CreateProviderAsync(
        Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> configure,
        CancellationToken loadCancellationToken,
        CancellationToken externalCancellationToken)
    {
        try
        {
            IConfigurationProvider provider = await configure.Invoke(_context)
                .WaitAsync(loadCancellationToken)
                .ConfigureAwait(false);

            ArgumentNullException.ThrowIfNull(provider);

            return provider;
        }
        catch (OperationCanceledException exception) when (externalCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The configuration manager add operation was canceled.",
                exception,
                externalCancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException(
                "The configuration manager timed out while creating a provider.",
                exception);
        }
    }

    private static async Task LoadProviderAsync(
        IConfigurationProvider provider,
        CancellationToken loadCancellationToken,
        CancellationToken externalCancellationToken)
    {
        try
        {
            await provider.LoadAsync(loadCancellationToken)
                .WaitAsync(loadCancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (!externalCancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The configuration provider '{provider.Name}' timed out during load.",
                exception);
        }
        catch (OperationCanceledException exception) when (externalCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The configuration manager add operation was canceled.",
                exception,
                externalCancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException(
                $"The configuration provider '{provider.Name}' timed out during load.",
                exception);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Failed to load configuration provider '{provider.Name}'.",
                exception);
        }
    }

    private static async Task DisposeProviderOnFailureAsync(IConfigurationProvider provider)
    {
        try
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
