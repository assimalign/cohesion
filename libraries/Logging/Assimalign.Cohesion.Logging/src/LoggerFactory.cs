using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Logging.Internal;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Default <see cref="ILoggerFactory"/>. Caches composite loggers per category and owns the
/// registered providers' lifecycle.
/// </summary>
public sealed class LoggerFactory : ILoggerFactory
{
    private readonly ConcurrentDictionary<string, ILogger> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LoggerFactoryOptions _options;
    private int _disposed;

    /// <summary>
    /// Initializes a factory with the supplied options. Most callers use
    /// <see cref="LoggerFactoryBuilder"/> instead of this constructor.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public LoggerFactory(LoggerFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<ILoggerProvider> Providers => _options.Providers;

    internal LoggerFactoryOptions Options => _options;

    /// <inheritdoc />
    public ILogger Create(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        ThrowIfDisposed();

        return _cache.GetOrAdd(category, static (key, factory) => factory.CreateComposite(key), this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var provider in _options.Providers)
        {
            try
            {
                provider.Dispose();
            }
            catch
            {
                // Provider disposal failures must not abort the rest of teardown.
            }
        }
    }

    internal LogLevel ResolveMinimumLevel(string category)
    {
        // Pick the longest matching prefix so the most specific filter wins.
        int bestLength = -1;
        LogLevel bestLevel = _options.MinimumLevel;

        foreach (var pair in _options.Filters)
        {
            if (category.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase) && pair.Key.Length > bestLength)
            {
                bestLength = pair.Key.Length;
                bestLevel = pair.Value;
            }
        }

        return bestLevel;
    }

    private CompositeLogger CreateComposite(string category)
    {
        var minimumLevel = ResolveMinimumLevel(category);

        var underlying = new ILogger[_options.Providers.Count];
        for (int i = 0; i < _options.Providers.Count; i++)
        {
            underlying[i] = _options.Providers[i].Create(category);
        }

        return new CompositeLogger(
            category: category,
            underlying: underlying,
            enrichers: _options.Enrichers,
            minimumLevel: minimumLevel);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LoggerFactory));
        }
    }
}
