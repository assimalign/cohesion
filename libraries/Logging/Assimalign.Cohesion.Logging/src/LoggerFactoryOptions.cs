using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Snapshot of registration-time configuration consumed by <see cref="LoggerFactory"/>.
/// </summary>
/// <remarks>
/// Populated by <see cref="LoggerFactoryBuilder"/>. Once the factory is built the options are
/// frozen; callers wishing to change levels or filters MUST build a new factory.
/// </remarks>
public sealed class LoggerFactoryOptions
{
    private static readonly IReadOnlyList<ILoggerProvider> EmptyProviders = Array.Empty<ILoggerProvider>();
    private static readonly IReadOnlyList<ILogEnricher> EmptyEnrichers = Array.Empty<ILogEnricher>();
    private static readonly IReadOnlyDictionary<string, LogLevel> EmptyFilters = new Dictionary<string, LogLevel>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new options snapshot.
    /// </summary>
    /// <param name="providers">The providers fan-out targets. Required.</param>
    /// <param name="enrichers">The enrichment pipeline in execution order. May be empty.</param>
    /// <param name="minimumLevel">Default minimum log level. Defaults to <see cref="LogLevel.Information"/>.</param>
    /// <param name="filters">Per-category overrides (case-insensitive prefix match).</param>
    public LoggerFactoryOptions(
        IReadOnlyList<ILoggerProvider>? providers = null,
        IReadOnlyList<ILogEnricher>? enrichers = null,
        LogLevel minimumLevel = LogLevel.Information,
        IReadOnlyDictionary<string, LogLevel>? filters = null)
    {
        Providers = providers ?? EmptyProviders;
        Enrichers = enrichers ?? EmptyEnrichers;
        MinimumLevel = minimumLevel;
        Filters = filters ?? EmptyFilters;
    }

    /// <summary>Providers the factory fans out to.</summary>
    public IReadOnlyList<ILoggerProvider> Providers { get; }

    /// <summary>Enrichers, in execution order.</summary>
    public IReadOnlyList<ILogEnricher> Enrichers { get; }

    /// <summary>Factory-wide minimum level.</summary>
    public LogLevel MinimumLevel { get; }

    /// <summary>Category prefix to minimum-level overrides.</summary>
    public IReadOnlyDictionary<string, LogLevel> Filters { get; }
}
