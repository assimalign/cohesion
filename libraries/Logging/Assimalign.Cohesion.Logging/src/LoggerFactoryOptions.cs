using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Configuration shape consumed by <see cref="LoggerFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// The options expose mutable collections so callers can populate <see cref="Providers"/> and
/// <see cref="Enrichers"/> directly (or through <see cref="LoggerFactoryBuilder"/>). After the
/// factory is constructed the lists are read by reference; mutating them post-construction is
/// not supported and may lead to undefined behavior.
/// </para>
/// </remarks>
public sealed class LoggerFactoryOptions
{
    /// <summary>Providers the factory fans out to. Add provider instances directly.</summary>
    public IList<ILoggerProvider> Providers { get; } = new List<ILoggerProvider>();

    /// <summary>Enrichers, in execution order. Add enricher instances directly.</summary>
    public IList<ILoggerEnricher> Enrichers { get; } = new List<ILoggerEnricher>();

    /// <summary>Factory-wide minimum level. Defaults to <see cref="LogLevel.Information"/>.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Optional per-entry filter. When non-null the filter sees every entry that already passed
    /// <see cref="MinimumLevel"/>; entries the filter rejects do not reach any provider.
    /// </summary>
    public ILoggerFilter? Filter { get; set; }
}
