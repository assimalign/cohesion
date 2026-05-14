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
/// <remarks>
/// <see cref="Create(string)"/> returns the concrete <see cref="LoggerBase"/> via covariant
/// return, so callers that hold a strongly typed <see cref="LoggerFactory"/> reference pay
/// only one virtual dispatch per log call. The factory still implements
/// <see cref="ILoggerFactory"/>; callers that hold the interface get an <see cref="ILogger"/>
/// (the same instance) through the synthesized interface bridge.
/// </remarks>
public sealed class LoggerFactory : ILoggerFactory
{
    private readonly ConcurrentDictionary<string, LoggerBase> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LoggerFactoryOptions _options;
    private readonly ILoggerProvider[] _providersSnapshot;
    private readonly ILoggerEnricher[] _enrichersSnapshot;
    private readonly LoggerFilterRule[] _rulesSnapshot;
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

        // Snapshot the lists so post-construction mutation of the options object cannot reshape
        // an already-running factory.
        _providersSnapshot = new ILoggerProvider[options.Providers.Count];
        options.Providers.CopyTo(_providersSnapshot, 0);

        _enrichersSnapshot = new ILoggerEnricher[options.Enrichers.Count];
        options.Enrichers.CopyTo(_enrichersSnapshot, 0);

        _rulesSnapshot = new LoggerFilterRule[options.FilterRules.Count];
        options.FilterRules.CopyTo(_rulesSnapshot, 0);
    }

    /// <inheritdoc />
    public IReadOnlyList<ILoggerProvider> Providers => _providersSnapshot;

    /// <summary>
    /// Returns the cached logger for <paramref name="category"/>, creating it from the
    /// registered providers on first use.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="LoggerBase"/> via covariant return; the call to
    /// <see cref="ILoggerFactory.Create(string)"/> still returns the same instance through the
    /// interface bridge.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    public LoggerBase Create(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        ThrowIfDisposed();

        return _cache.GetOrAdd(category, static (key, factory) => factory.CreateComposite(key), this);
    }

    ILogger ILoggerFactory.Create(string category) => Create(category);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var provider in _providersSnapshot)
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

    private CompositeLogger CreateComposite(string category)
    {
        var providerCount = _providersSnapshot.Length;
        var underlying = new ILogger[providerCount];
        var perProviderLevel = new LogLevel[providerCount];
        var perProviderFilter = new ILoggerFilter?[providerCount];

        for (int i = 0; i < providerCount; i++)
        {
            var provider = _providersSnapshot[i];
            underlying[i] = provider.Create(category);

            // Pre-resolve the winning rule per (provider type, category). Storing the resolved
            // level + filter alongside the underlying logger keeps fan-out O(providers) at log
            // time, with no per-entry rule lookup.
            var rule = LoggerFilterRuleSelector.Select(_rulesSnapshot, provider.GetType(), category);
            perProviderLevel[i] = rule?.Level ?? _options.MinimumLevel;
            perProviderFilter[i] = rule?.Filter;
        }

        return new CompositeLogger(
            category: category,
            underlying: underlying,
            enrichers: _enrichersSnapshot,
            perProviderLevel: perProviderLevel,
            perProviderFilter: perProviderFilter);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LoggerFactory));
        }
    }
}
