using System;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The Database hosting module's diagnostics: what the application does about a database its
/// engine reports offline (owner decision 22 of 2026-10-06). Every reopen attempt and every
/// outcome is one event.
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>): a
/// library describes its own behavior through an event source, never a logging dependency. Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Hosting</c>; applications
/// forward it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>, under the
/// same category name. The engine's own events (a worker's failures, the database it took
/// offline) are on the <c>Assimalign.Cohesion.Database</c> source.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Hosting")]
internal sealed class DatabaseHostingEventSource : EventSource
{
    public static readonly DatabaseHostingEventSource Log = new();

    private DatabaseHostingEventSource()
    {
    }

    /// <summary>
    /// Writes that the application found a database offline and will reopen it.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="cause">What took it offline, or null when the engine does not say.</param>
    /// <param name="delay">How long the application waits before its first reopen.</param>
    [NonEvent]
    public void OfflineDatabaseFound(IDatabaseEngine engine, string database, StorageOfflineCause? cause, TimeSpan delay)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            OfflineDatabaseFound(engine.Name, database, cause?.ToString() ?? "Unknown", (long)delay.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that a reopen attempt starts.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="attempt">The attempt's number for this database, from one.</param>
    [NonEvent]
    public void ReopenAttempted(IDatabaseEngine engine, string database, int attempt)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ReopenAttempted(engine.Name, database, attempt);
        }
    }

    /// <summary>
    /// Writes that a reopen succeeded: the database is open again.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="attempts">How many attempts it took.</param>
    [NonEvent]
    public void ReopenSucceeded(IDatabaseEngine engine, string database, int attempts)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ReopenSucceeded(engine.Name, database, attempts);
        }
    }

    /// <summary>
    /// Writes that a reopen attempt failed, and when the next one runs.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="attempt">The failed attempt's number.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="retryDelay">How long the application waits before the next attempt.</param>
    [NonEvent]
    public void ReopenFailed(IDatabaseEngine engine, string database, int attempt, Exception exception, TimeSpan retryDelay)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ReopenFailed(
                engine.Name,
                database,
                attempt,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message,
                (long)retryDelay.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that the application stopped reopening a database without reopening it: it was
    /// dropped, closed by its holder or reopened by someone else, or its engine was disposed.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="reason">Why.</param>
    [NonEvent]
    public void ReopenAbandoned(IDatabaseEngine engine, string database, string reason)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ReopenAbandoned(engine.Name, database, reason);
        }
    }

    /// <summary>
    /// Writes a failure of the reopen loop itself, which goes on after it.
    /// </summary>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void ReopenLoopFailed(Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ReopenLoopFailed(exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Warning, Message = "Database '{1}' of engine {0} is offline ({2}); the application reopens it in {3} ms, with backoff.")]
    private void OfflineDatabaseFound(string engineName, string database, string cause, long delayMilliseconds)
        => WriteEvent(1, engineName, database, cause, delayMilliseconds);

    [Event(2, Level = EventLevel.Informational, Message = "Reopening database '{1}' of engine {0}: attempt {2}.")]
    private void ReopenAttempted(string engineName, string database, int attempt)
        => WriteEvent(2, engineName, database, attempt);

    [Event(3, Level = EventLevel.Informational, Message = "Database '{1}' of engine {0} is open again after {2} reopen attempt(s).")]
    private void ReopenSucceeded(string engineName, string database, int attempts)
        => WriteEvent(3, engineName, database, attempts);

    [Event(4, Level = EventLevel.Warning, Message = "Reopening database '{1}' of engine {0} failed on attempt {2}: {3}: {4}. The next attempt runs in {5} ms.")]
    private void ReopenFailed(string engineName, string database, int attempt, string exceptionType, string exceptionMessage, long retryMilliseconds)
        => WriteEvent(4, engineName, database, attempt, exceptionType, exceptionMessage, retryMilliseconds);

    [Event(5, Level = EventLevel.Informational, Message = "Database '{1}' of engine {0} is no longer reopened: {2}.")]
    private void ReopenAbandoned(string engineName, string database, string reason)
        => WriteEvent(5, engineName, database, reason);

    [Event(6, Level = EventLevel.Error, Message = "The database reopen loop failed: {0}: {1}. It goes on.")]
    private void ReopenLoopFailed(string exceptionType, string exceptionMessage)
        => WriteEvent(6, exceptionType, exceptionMessage);
}
