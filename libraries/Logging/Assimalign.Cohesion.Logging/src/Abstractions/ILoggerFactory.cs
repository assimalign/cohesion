using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Roots the Cohesion logging pipeline. Provides cached, composite loggers for callers and owns
/// the registered <see cref="ILoggerProvider"/>s.
/// </summary>
public interface ILoggerFactory : IDisposable
{
    /// <summary>
    /// The providers fan-out targets registered with this factory.
    /// </summary>
    IReadOnlyList<ILoggerProvider> Providers { get; }

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
