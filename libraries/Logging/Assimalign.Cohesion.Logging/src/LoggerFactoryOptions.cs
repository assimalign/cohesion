using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Configuration shape consumed by <see cref="LoggerFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// The options expose mutable collections so callers can populate <see cref="Providers"/>,
/// <see cref="Enrichers"/>, and <see cref="FilterRules"/> directly (or through
/// <see cref="LoggerFactoryBuilder"/>). After the factory is constructed the lists are read by
/// reference; mutating them post-construction is not supported and may lead to undefined
/// behavior.
/// </para>
/// </remarks>
public sealed class LoggerFactoryOptions
{
    /// <summary>Providers the factory fans out to. Add provider instances directly.</summary>
    public IList<ILoggerProvider> Providers { get; } = new List<ILoggerProvider>();

    /// <summary>Enrichers, in execution order. Add enricher instances directly.</summary>
    public IList<ILoggerEnricher> Enrichers { get; } = new List<ILoggerEnricher>();

    /// <summary>
    /// Filter rules consulted per (provider, category) pair. The factory resolves a single
    /// winning rule per pair via <see cref="LoggerFilterRule"/>'s selection algorithm; when no
    /// rule applies, <see cref="MinimumLevel"/> is used as the gate.
    /// </summary>
    public IList<LoggerFilterRule> FilterRules { get; } = new List<LoggerFilterRule>();

    /// <summary>Factory-wide minimum level. Used as the fallback when no <see cref="FilterRules"/> rule matches a (provider, category) pair. Defaults to <see cref="LogLevel.Information"/>.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
