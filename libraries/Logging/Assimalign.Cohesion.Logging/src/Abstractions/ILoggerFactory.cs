using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Roots the Cohesion logging pipeline. Provides cached, composite loggers for callers and owns
/// the registered <see cref="ILoggerProvider"/>s and <see cref="ILoggerForwarder"/>s.
/// </summary>
/// <remarks>
/// The factory's composition is fixed when it is constructed. <see cref="Providers"/>,
/// <see cref="Enrichers"/>, <see cref="Rules"/>, and <see cref="Forwarders"/> are read-only views of
/// it, in registration order.
/// </remarks>
public interface ILoggerFactory : IDisposable
{
    /// <summary>
    /// The providers fan-out targets registered with this factory.
    /// </summary>
    IReadOnlyList<ILoggerProvider> Providers { get; }

    /// <summary>
    /// The enrichers every entry passes through before fan-out, in execution order.
    /// </summary>
    IReadOnlyList<ILoggerEnricher> Enrichers { get; }

    /// <summary>
    /// The filter rules the factory resolves per (provider, category) pair. A rule's optional
    /// <see cref="LoggerFilterRule.Filter"/> is where each <see cref="ILoggerFilter"/> lives.
    /// </summary>
    IReadOnlyList<LoggerFilterRule> Rules { get; }

    /// <summary>
    /// The forwarders the factory owns, in creation order. A forwarder the caller created against the
    /// factory, rather than registered with it, is not listed.
    /// </summary>
    IReadOnlyList<ILoggerForwarder> Forwarders { get; }

    /// <summary>
    /// Returns the cached logger for the supplied category, creating it from the registered
    /// providers on first use.
    /// </summary>
    /// <param name="category">The category that uniquely identifies the calling component. Required.</param>
    /// <returns>
    /// A composite logger that fans out to every registered provider. Repeated calls with the same
    /// (case-insensitive) category return the same instance.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    ILogger Create(string category);
}
