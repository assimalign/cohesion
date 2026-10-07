using System;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Internal;

/// <summary>
/// The Database area root's diagnostics: the failures and recoveries of engine background workers
/// (<see cref="DatabaseEngineWorker"/>, #1268), and the databases an engine takes offline once a
/// worker's failures on them persist (owner decision 25 of 2026-10-06).
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database</c>; applications forward it into
/// their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Every engine model's workers
/// derive from the root's guided base, which raises these events, so one source covers the workers
/// of all five engines. A worker's failure is retried, so each failure is a
/// <see cref="EventLevel.Warning"/>, written at most once per <see cref="DatabaseEngineWorker.FailureBackoff"/>
/// per database for a failure that repeats; the recovery that ends it is
/// <see cref="EventLevel.Informational"/>. PostgreSQL reports every error of a background worker's
/// cycle the same way before it sleeps and retries (<c>src/backend/postmaster/checkpointer.c:294-295</c>).
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database")]
internal sealed class DatabaseEventSource : EventSource
{
    public static readonly DatabaseEventSource Log = new();

    private DatabaseEventSource()
    {
    }

    /// <summary>
    /// Writes a failure of an engine worker: of one database's work, or of a whole pass when
    /// <paramref name="database"/> is empty.
    /// </summary>
    /// <param name="worker">The worker that failed.</param>
    /// <param name="database">The database whose work failed; empty for a failure of the whole pass or of its trigger wait.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="consecutiveFailures">How many times in a row this work has failed, this failure included.</param>
    [NonEvent]
    public void WorkerFailed(DatabaseEngineWorker worker, string database, Exception exception, int consecutiveFailures)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            WorkerFailed(
                worker.Name,
                worker.Kind.ToString(),
                database,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message,
                consecutiveFailures);
        }
    }

    /// <summary>
    /// Writes the recovery of an engine worker: a pass finished the work a failure of one database,
    /// or of the worker's passes when <paramref name="database"/> is empty, had left.
    /// </summary>
    /// <param name="worker">The worker that recovered.</param>
    /// <param name="database">The database whose work completed; empty for the worker's passes.</param>
    /// <param name="failures">How many times in a row the work had failed before it completed.</param>
    [NonEvent]
    public void WorkerRecovered(DatabaseEngineWorker worker, string database, int failures)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            WorkerRecovered(worker.Name, worker.Kind.ToString(), database, failures);
        }
    }

    /// <summary>
    /// Writes that an engine took a database offline because it gave up on it (owner decision 25
    /// of 2026-10-06): a worker's work on the database kept failing for the engine's window across
    /// its minimum of failed passes (owner decision 42 of 2026-10-07), or its journal passed the
    /// engine's cap while its checkpoints kept failing.
    /// </summary>
    /// <param name="engine">The engine that took the database offline.</param>
    /// <param name="worker">The worker whose work on the database kept failing.</param>
    /// <param name="database">The database.</param>
    /// <param name="cause">The cause the database went offline with.</param>
    /// <param name="failure">The worker's last failure.</param>
    [NonEvent]
    public void DatabaseTakenOffline(DatabaseEngine engine, DatabaseEngineWorker worker, string database, StorageOfflineCause cause, Exception failure)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            DatabaseTakenOffline(
                engine.Name,
                database,
                cause.ToString(),
                worker.Name,
                worker.Kind.ToString(),
                failure.GetType().FullName ?? failure.GetType().Name,
                failure.Message);
        }
    }

    [Event(1, Level = EventLevel.Warning, Message = "Database engine worker {0} ({1}) failed on database '{2}': {3}: {4}. Failure {5} in a row; the worker retries.")]
    private void WorkerFailed(string workerName, string workerKind, string database, string exceptionType, string exceptionMessage, int consecutiveFailures)
        => WriteEvent(1, workerName, workerKind, database, exceptionType, exceptionMessage, consecutiveFailures);

    [Event(2, Level = EventLevel.Informational, Message = "Database engine worker {0} ({1}) recovered on database '{2}' after {3} failure(s) in a row.")]
    private void WorkerRecovered(string workerName, string workerKind, string database, int failures)
        => WriteEvent(2, workerName, workerKind, database, failures);

    [Event(3, Level = EventLevel.Error, Message = "Database engine {0} took database '{1}' offline ({2}): its worker {3} ({4}) kept failing on it; the last failure was {5}: {6}. Every operation on the database is refused until it is reopened.")]
    private void DatabaseTakenOffline(string engineName, string database, string cause, string workerName, string workerKind, string exceptionType, string exceptionMessage)
        => WriteEvent(3, engineName, database, cause, workerName, workerKind, exceptionType, exceptionMessage);
}
