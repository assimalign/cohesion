using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Writes <see cref="ILoggerEntry"/> events to one or more sinks.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be thread-safe. Loggers are typically obtained from
/// <see cref="ILoggerFactory.Create(string)"/> with a category name; the factory caches them so
/// that multiple calls with the same name return the same instance.
/// </para>
/// <para>
/// A logger that fans out to multiple <see cref="ILoggerProvider"/>s honors <see cref="IsEnabled"/>
/// as "any underlying logger is enabled" so a single fan-out logger never produces stale answers.
/// </para>
/// </remarks>
public interface ILogger
{
    /// <summary>
    /// Returns <see langword="true"/> when at least one underlying sink would accept an entry at
    /// the supplied <paramref name="level"/>. Loggers SHOULD short-circuit on <see langword="false"/>
    /// to avoid materializing expensive log payloads.
    /// </summary>
    bool IsEnabled(LogLevel level);

    /// <summary>
    /// Writes <paramref name="entry"/> to every underlying sink. Implementations MUST NOT throw
    /// when a single sink fails; failures are isolated to the sink that raised them.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    void Log(ILoggerEntry entry);

    /// <summary>
    /// Opens a logical scope. Entries produced inside the scope share the scope's
    /// <see cref="IScopedLogger.ParentId"/> through their <see cref="ILoggerEntry.ParentId"/>.
    /// </summary>
    /// <param name="entry">The seed entry that opens the scope. Required.</param>
    /// <returns>A disposable scoped logger; disposing the scope closes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    IScopedLogger BeginScope(ILoggerEntry entry);
}
