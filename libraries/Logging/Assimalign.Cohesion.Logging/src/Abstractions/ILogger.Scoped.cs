using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// A logger bound to a parent scope. Entries written through the scoped logger inherit the
/// parent's <see cref="ParentId"/> as their <see cref="ILoggerEntry.ParentId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Scoped loggers are obtained from <see cref="ILogger.BeginScope(ILoggerEntry)"/>. Disposing the
/// scoped logger closes the scope; calling <see cref="IDisposable.Dispose"/> more than once is a
/// no-op. Scopes can nest: calling <see cref="ILogger.BeginScope(ILoggerEntry)"/> on an
/// <see cref="IScopedLogger"/> returns a new scope whose <see cref="ParentId"/> is the seed
/// entry's id.
/// </para>
/// </remarks>
public interface IScopedLogger : ILogger, IDisposable
{
    /// <summary>
    /// Id of the seed entry that opened the scope.
    /// </summary>
    LogId ParentId { get; }
}
