using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Assimalign.Cohesion.Logging.Internal;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Default <see cref="ILoggerFactory"/>. Caches composite loggers per category and owns the
/// lifecycle of the registered providers and forwarders.
/// </summary>
/// <remarks>
/// <see cref="Create(string)"/> returns the concrete <see cref="Logger"/> via covariant
/// return, so callers that hold a strongly typed <see cref="LoggerFactory"/> reference pay
/// only one virtual dispatch per log call. The factory still implements
/// <see cref="ILoggerFactory"/>; callers that hold the interface get an <see cref="ILogger"/>
/// (the same instance) through the synthesized interface bridge.
/// </remarks>
public sealed class LoggerFactory : ILoggerFactory
{
    private readonly ConcurrentDictionary<string, Logger> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LoggerFactoryOptions _options;
    private readonly ILoggerProvider[] _providersSnapshot;
    private readonly ILoggerEnricher[] _enrichersSnapshot;
    private readonly LoggerFilterRule[] _rulesSnapshot;
    private readonly ILoggerForwarder[] _forwarders = Array.Empty<ILoggerForwarder>();

    // Read-only views over the snapshots. Returning the arrays themselves would let a caller cast
    // back and change what running loggers already hold.
    private readonly ReadOnlyCollection<ILoggerProvider> _providers;
    private readonly ReadOnlyCollection<ILoggerEnricher> _enrichers;
    private readonly ReadOnlyCollection<LoggerFilterRule> _rules;
    private readonly ReadOnlyCollection<ILoggerForwarder> _forwardersView = ReadOnlyCollection<ILoggerForwarder>.Empty;
    private int _disposed;

    /// <summary>
    /// Initializes a factory with the supplied options. Most callers use
    /// <see cref="LoggerFactoryBuilder"/> instead of this constructor.
    /// </summary>
    /// <remarks>
    /// The registrations in <see cref="LoggerFactoryOptions.Forwarders"/> are invoked, in order,
    /// as the last step of construction. If one throws, the forwarders already created and the
    /// providers are disposed and the exception propagates.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A forwarder registration is <see langword="null"/> or returned <see langword="null"/>, or two forwarders have the same <see cref="ILoggerForwarder.Name"/>.</exception>
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

        _providers = Array.AsReadOnly(_providersSnapshot);
        _enrichers = Array.AsReadOnly(_enrichersSnapshot);
        _rules = Array.AsReadOnly(_rulesSnapshot);

        // Last, because each forwarder receives this factory and may create loggers from it
        // immediately; everything above must already be in place.
        _forwarders = CreateForwarders(options.Forwarders);
        _forwardersView = Array.AsReadOnly(_forwarders);
    }

    /// <inheritdoc />
    public IReadOnlyList<ILoggerProvider> Providers => _providers;

    /// <inheritdoc />
    public IReadOnlyList<ILoggerEnricher> Enrichers => _enrichers;

    /// <inheritdoc />
    public IReadOnlyList<LoggerFilterRule> Rules => _rules;

    /// <inheritdoc />
    /// <remarks>Empty while the forwarders are being created.</remarks>
    public IReadOnlyList<ILoggerForwarder> Forwarders => _forwardersView;

    /// <summary>
    /// Returns the cached logger for <paramref name="category"/>, creating it from the
    /// registered providers on first use.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="Logger"/> via covariant return; the call to
    /// <see cref="ILoggerFactory.Create(string)"/> still returns the same instance through the
    /// interface bridge.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    public Logger Create(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        ThrowIfDisposed();

        return _cache.GetOrAdd(category, static (key, factory) => factory.CreateComposite(key), this);
    }

    ILogger ILoggerFactory.Create(string category) => Create(category);

    /// <summary>
    /// Disposes the factory's forwarders, newest first, and then its providers. A component that
    /// throws while being disposed does not stop the rest. Calling this more than once has no
    /// further effect.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Forwarders write through the providers, so they stop first.
        DisposeForwarders(_forwarders, _forwarders.Length);
        DisposeProviders();
    }

    private ILoggerForwarder[] CreateForwarders(IList<Func<ILoggerFactory, ILoggerForwarder>> registrations)
    {
        if (registrations.Count == 0)
        {
            return Array.Empty<ILoggerForwarder>();
        }

        // Snapshot first: a registration may touch the options while it runs.
        var snapshot = new Func<ILoggerFactory, ILoggerForwarder>[registrations.Count];
        registrations.CopyTo(snapshot, 0);

        var forwarders = new ILoggerForwarder[snapshot.Length];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = 0;

        try
        {
            foreach (var create in snapshot)
            {
                if (create is null)
                {
                    throw new InvalidOperationException("A forwarder registration is null.");
                }

                var forwarder = create(this)
                    ?? throw new InvalidOperationException("A forwarder registration returned null.");
                forwarders[created++] = forwarder;

                // Checked after the forwarder is recorded, so a duplicate is released with the rest.
                if (!names.Add(forwarder.Name ?? string.Empty))
                {
                    throw new InvalidOperationException(
                        $"A forwarder named '{forwarder.Name}' is already registered.");
                }
            }
        }
        catch
        {
            // A factory that fails to construct is never returned, so nothing else can release
            // what it already owns. Anything a forwarder captured sees a disposed factory.
            Volatile.Write(ref _disposed, 1);
            DisposeForwarders(forwarders, created);
            DisposeProviders();
            throw;
        }

        return forwarders;
    }

    private static void DisposeForwarders(ILoggerForwarder[] forwarders, int count)
    {
        // Newest first, the reverse of creation, so a forwarder never outlives one created
        // before it.
        for (var i = count - 1; i >= 0; i--)
        {
            try
            {
                forwarders[i].Dispose();
            }
            catch
            {
                // Forwarder disposal failures must not abort the rest of teardown.
            }
        }
    }

    private void DisposeProviders()
    {
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
