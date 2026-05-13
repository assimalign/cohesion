using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Default <see cref="ILoggerFactoryBuilder"/> implementation. Build the factory in a single
/// thread; the resulting <see cref="ILoggerFactory"/> is the thread-safe handle for callers.
/// </summary>
public sealed class LoggerFactoryBuilder : ILoggerFactoryBuilder
{
    private readonly List<ILoggerProvider> _providers = new();
    private readonly List<ILogEnricher> _enrichers = new();
    private readonly Dictionary<string, LogLevel> _filters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _providerNames = new(StringComparer.OrdinalIgnoreCase);
    private LogLevel _minimumLevel = LogLevel.Information;
    private bool _built;

    /// <inheritdoc />
    public ILoggerFactoryBuilder AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ThrowIfBuilt();

        if (!_providerNames.Add(provider.Name ?? string.Empty))
        {
            throw new InvalidOperationException(
                $"A provider named '{provider.Name}' is already registered.");
        }

        _providers.Add(provider);
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactoryBuilder SetMinimumLevel(LogLevel level)
    {
        ThrowIfBuilt();
        _minimumLevel = level;
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactoryBuilder AddFilter(string categoryPrefix, LogLevel minimumLevel)
    {
        ArgumentException.ThrowIfNullOrEmpty(categoryPrefix);
        ThrowIfBuilt();
        _filters[categoryPrefix] = minimumLevel;
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactoryBuilder AddEnricher(ILogEnricher enricher)
    {
        ArgumentNullException.ThrowIfNull(enricher);
        ThrowIfBuilt();
        _enrichers.Add(enricher);
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactory Build()
    {
        ThrowIfBuilt();
        _built = true;

        return new LoggerFactory(new LoggerFactoryOptions(
            providers: _providers.ToArray(),
            enrichers: _enrichers.ToArray(),
            minimumLevel: _minimumLevel,
            filters: new Dictionary<string, LogLevel>(_filters, StringComparer.OrdinalIgnoreCase)));
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "LoggerFactoryBuilder has already been used to build a factory; create a new builder.");
        }
    }
}
