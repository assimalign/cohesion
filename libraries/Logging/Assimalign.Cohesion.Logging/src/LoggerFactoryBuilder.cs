using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Default <see cref="ILoggerFactoryBuilder"/> implementation. Populates a fresh
/// <see cref="LoggerFactoryOptions"/> through the fluent API and materializes the factory in
/// <see cref="Build"/>.
/// </summary>
/// <remarks>
/// Builders are not thread-safe; treat them as scratch space scoped to a single setup routine.
/// The resulting <see cref="ILoggerFactory"/> IS thread-safe.
/// </remarks>
public sealed class LoggerFactoryBuilder : ILoggerFactoryBuilder
{
    private readonly LoggerFactoryOptions _options = new();
    private readonly HashSet<string> _providerNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, LogLevel>> _categoryRules = new();
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

        _options.Providers.Add(provider);
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactoryBuilder SetMinimumLevel(LogLevel level)
    {
        ThrowIfBuilt();
        _options.MinimumLevel = level;
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactoryBuilder AddFilter(string categoryPrefix, LogLevel minimumLevel)
    {
        ArgumentException.ThrowIfNullOrEmpty(categoryPrefix);
        ThrowIfBuilt();
        _categoryRules.Add(new KeyValuePair<string, LogLevel>(categoryPrefix, minimumLevel));
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactoryBuilder UseFilter(ILoggerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ThrowIfBuilt();
        _options.Filter = filter;
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactoryBuilder AddEnricher(ILoggerEnricher enricher)
    {
        ArgumentNullException.ThrowIfNull(enricher);
        ThrowIfBuilt();
        _options.Enrichers.Add(enricher);
        return this;
    }

    /// <inheritdoc />
    public ILoggerFactory Build()
    {
        ThrowIfBuilt();
        _built = true;

        // Compose any AddFilter category rules into a CategoryLoggerFilter and combine with an
        // explicitly supplied filter (if any). When both are present, the explicit filter wins
        // first and the category rules act as a fallback.
        if (_categoryRules.Count > 0)
        {
            var categoryFilter = new CategoryLoggerFilter(_categoryRules);
            _options.Filter = _options.Filter is null
                ? categoryFilter
                : new CompositeLoggerFilter(_options.Filter, categoryFilter);
        }

        return new LoggerFactory(_options);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "LoggerFactoryBuilder has already been used to build a factory; create a new builder.");
        }
    }

    private sealed class CompositeLoggerFilter : ILoggerFilter
    {
        private readonly ILoggerFilter _first;
        private readonly ILoggerFilter _second;

        public CompositeLoggerFilter(ILoggerFilter first, ILoggerFilter second)
        {
            _first = first;
            _second = second;
        }

        public bool ShouldLog(ILoggerEntry entry) => _first.ShouldLog(entry) && _second.ShouldLog(entry);
    }
}
