using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Source of <see cref="ILogger"/> instances for a single output sink (console, debug stream,
/// file, structured collector, ...).
/// </summary>
/// <remarks>
/// Providers are registered with the factory at build time and are owned by the resulting
/// <see cref="ILoggerFactory"/>. Disposal flows from the factory: when the factory is disposed,
/// it disposes every provider it owns.
/// </remarks>
public interface ILoggerProvider : IDisposable
{
    /// <summary>
    /// Stable provider name used for diagnostics and registration de-duplication.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns a logger for the supplied <paramref name="category"/>.
    /// </summary>
    /// <remarks>
    /// Implementations MAY cache loggers internally; the factory also caches the composite that
    /// fans out across every provider, so a provider returning a fresh instance per call is safe
    /// but wasteful.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    ILogger Create(string category);
}
