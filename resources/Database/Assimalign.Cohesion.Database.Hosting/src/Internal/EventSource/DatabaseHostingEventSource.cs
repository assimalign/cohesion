using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The Database hosting module's diagnostics: application lifecycle, resource commands, the
/// admin endpoint, and each attempt to reopen a database its engine reports offline.
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
    public void OfflineDatabaseFound(DatabaseEngine engine, string database, StorageOfflineCause? cause, TimeSpan delay)
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
    public void ReopenAttempted(DatabaseEngine engine, string database, int attempt)
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
    public void ReopenSucceeded(DatabaseEngine engine, string database, int attempts)
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
    public void ReopenFailed(DatabaseEngine engine, string database, int attempt, Exception exception, TimeSpan retryDelay)
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
    public void ReopenAbandoned(DatabaseEngine engine, string database, string reason)
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

    [NonEvent]
    public long LifecycleTimestamp()
        => IsEnabled(EventLevel.Informational, EventKeywords.None) ? Stopwatch.GetTimestamp() : 0;

    [NonEvent]
    public void ApplicationBuilt(DatabaseApplicationContext context, int services)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ApplicationBuilt(context.HostId.ToString(), context.Engines.Count, context.Servers.Count, services);
        }
    }

    [NonEvent]
    public void ApplicationStarting(DatabaseApplicationContext context)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ApplicationStarting(context.HostId.ToString());
        }
    }

    [NonEvent]
    public void ApplicationStarted(DatabaseApplicationContext context, long started)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ApplicationStarted(context.HostId.ToString(), Elapsed(started));
        }
    }

    [NonEvent]
    public void ApplicationStartFailed(DatabaseApplicationContext context, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ApplicationStartFailed(context.HostId.ToString(), exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [NonEvent]
    public void ApplicationStopped(DatabaseApplicationContext context, long started)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ApplicationStopped(context.HostId.ToString(), Elapsed(started));
        }
    }

    [NonEvent]
    public void ApplicationDisposeFailed(DatabaseApplicationContext context, int failureCount, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ApplicationDisposeFailed(context.HostId.ToString(), failureCount, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [NonEvent]
    public void ResourceCommandHandled(ResourceCommand command, string operation)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ResourceCommandHandled(Bound(command.Kind), Bound(command.Key), operation);
        }
    }

    [NonEvent]
    public void ResourceCommandRejected(ResourceCommand command, string operation, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            // A command's message may repeat its payload. Keep only its type (plan D8).
            ResourceCommandRejected(Bound(command.Kind), Bound(command.Key), operation, exception.GetType().FullName ?? exception.GetType().Name);
        }
    }

    [NonEvent]
    public void AdminEndpointFailed(Uri endpoint, string operation, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            AdminEndpointFailed(endpoint.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped), operation,
                exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
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

    [Event(7, Level = EventLevel.Informational, Message = "Database application {0} built with {1} engine(s), {2} server(s) and {3} service(s).")]
    private void ApplicationBuilt(string hostId, int engines, int servers, int services)
        => WriteEvent(7, hostId, engines, servers, services);

    [Event(8, Level = EventLevel.Informational, Message = "Database application {0} is starting.")]
    private void ApplicationStarting(string hostId)
        => WriteEvent(8, hostId);

    [Event(9, Level = EventLevel.Informational, Message = "Database application {0} started in {1} ms.")]
    private void ApplicationStarted(string hostId, double durationMilliseconds)
        => WriteEvent(9, hostId, durationMilliseconds);

    [Event(10, Level = EventLevel.Error, Message = "Database application {0} failed to start: {1}: {2}")]
    private void ApplicationStartFailed(string hostId, string exceptionType, string exceptionMessage)
        => WriteEvent(10, hostId, exceptionType, exceptionMessage);

    [Event(11, Level = EventLevel.Informational, Message = "Database application {0} stopped in {1} ms.")]
    private void ApplicationStopped(string hostId, double durationMilliseconds)
        => WriteEvent(11, hostId, durationMilliseconds);

    [Event(12, Level = EventLevel.Error, Message = "Database application {0} disposal had {1} failure(s); first: {2}: {3}")]
    private void ApplicationDisposeFailed(string hostId, int failureCount, string exceptionType, string exceptionMessage)
        => WriteEvent(12, hostId, failureCount, exceptionType, exceptionMessage);

    [Event(13, Level = EventLevel.Informational, Message = "Handled resource command {0} '{1}' ({2}).")]
    private void ResourceCommandHandled(string kind, string key, string operation)
        => WriteEvent(13, kind, key, operation);

    [Event(14, Level = EventLevel.Warning, Message = "Rejected resource command {0} '{1}' ({2}): {3}")]
    private void ResourceCommandRejected(string kind, string key, string operation, string exceptionType)
        => WriteEvent(14, kind, key, operation, exceptionType);

    [Event(15, Level = EventLevel.Error, Message = "Database admin endpoint {0} failed during {1}: {2}: {3}")]
    private void AdminEndpointFailed(string endpoint, string operation, string exceptionType, string exceptionMessage)
        => WriteEvent(15, endpoint, operation, exceptionType, exceptionMessage);

    // Ids 16-17 are reserved for HealthDegraded / HealthRestored after decision 49a.

    private static double Elapsed(long started) => started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static string Bound(string value)
    {
        const int maxLength = 256;
        if (value.Length <= maxLength)
        {
            return value;
        }

        int length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return string.Concat(value.AsSpan(0, length), "...");
    }
}
