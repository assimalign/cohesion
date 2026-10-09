using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Internal;

/// <summary>
/// The Database area root's diagnostics: the lifecycle of engines, databases, servers, sessions and
/// explicit transactions, the statements every model runs through the root session, and the
/// failures and recoveries of engine background workers (<see cref="DatabaseEngineWorker"/>,
/// #1268), including the databases an engine takes offline once a worker's failures on them persist
/// (owner decision 25 of 2026-10-06).
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database</c>; applications forward it into
/// their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Every model's engine, worker,
/// server, session and transaction derives from the root's bases, which raise these events, so one
/// source covers all five models (event-sources plan, D1). A worker's failure is retried, so each
/// failure is a <see cref="EventLevel.Warning"/>, written at most once per
/// <see cref="DatabaseEngineWorker.FailureBackoff"/> per database for a failure that repeats; the
/// recovery that ends it is <see cref="EventLevel.Informational"/>. PostgreSQL reports every error of
/// a background worker's cycle the same way before it sleeps and retries
/// (<c>src/backend/postmaster/checkpointer.c:294-295</c>).
/// </para>
/// <para>
/// <b>Cost.</b> Every write is behind <see cref="EventSource.IsEnabled(EventLevel, EventKeywords)"/>,
/// and every string a payload needs is computed inside that check. The members that time their
/// work (statements, database create, open and drop) return their core directly while the source is
/// disabled; while it is enabled at <see cref="EventLevel.Error"/> or a more verbose level they call
/// the core the same way and enter a pooled async wrapper only for a core that has not completed
/// successfully, so a core that throws synchronously throws from the call whether or not anyone
/// listens (rule 12); the open, asynchronous untraced too, is one pooled wrapper.
/// Timestamps are taken only inside such a check. A failure is captured by an exception filter
/// (<see cref="CaptureFailure"/>) and written from the <c>finally</c> that follows, once the core's
/// own <c>finally</c> blocks released the locks they held. The one counter, <c>current-sessions</c>,
/// is maintained whether or not anyone listens.
/// </para>
/// <para>
/// <b>Arguments.</b> <c>SlowStatementThresholdMs</c> sets how long a statement runs before
/// <c>SlowStatement</c> reports it (default <see cref="DefaultSlowStatementThresholdMilliseconds"/>,
/// event-sources plan D7). Each enabling session sets it: to its argument, or to the default when it
/// passes none, so the last session to enable the source wins. A session that disables the source
/// restores the default, so a brief tool session's threshold never outlives it.
/// </para>
/// <para>
/// <b>Payloads</b> never carry statement text, parameter values, keys, values or authentication
/// evidence (rule 11; plan D8, owner question Q3). An exception is written as its type's full name
/// and its <see cref="Exception.Message"/>, except where the message can quote the statement: a
/// parser quotes the token it stopped at, string literals included, and every model's
/// aborted-transaction refusal repeats the failed operation's message. A failed statement is
/// therefore identified by its request kind, its session and its diagnostic code or exception type;
/// an aborted transaction by its cause's type; and a commit refused because an operation aborted the
/// transaction writes an empty message.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database")]
internal sealed class DatabaseEventSource : EventSource
{
    /// <summary>
    /// The slow-statement threshold an enabling session gets when it passes no
    /// <c>SlowStatementThresholdMs</c> argument: one second (event-sources plan D7, Q2).
    /// </summary>
    public const double DefaultSlowStatementThresholdMilliseconds = 1000;

    /// <summary>
    /// The name of the EventSource argument that sets the slow-statement threshold, in milliseconds.
    /// </summary>
    public const string SlowStatementThresholdArgument = "SlowStatementThresholdMs";

    /// <summary>
    /// The request kind of a statement run through the session's text overload of
    /// <c>ExecuteAsync</c>; a typed request's kind is its type's name.
    /// </summary>
    public const string TextRequestKind = "Text";

    public static readonly DatabaseEventSource Log = new();

    private PollingCounter? _currentSessionsCounter;
    private long _currentSessions;
    private double _slowStatementThresholdMilliseconds = DefaultSlowStatementThresholdMilliseconds;

    private DatabaseEventSource()
    {
    }

    /// <summary>
    /// Gets the sessions created and not yet closed, process-wide: the <c>current-sessions</c> gauge.
    /// </summary>
    internal long CurrentSessions => Interlocked.Read(ref _currentSessions);

    /// <summary>
    /// Gets how long a statement runs before <c>SlowStatement</c> reports it, as the last enabling
    /// session set it.
    /// </summary>
    internal double SlowStatementThresholdMilliseconds => Volatile.Read(ref _slowStatementThresholdMilliseconds);

    /// <summary>
    /// Takes a timestamp for an event that reports a duration, only while the source is enabled for
    /// that event: 0 otherwise, which the matching write reads as "not timed".
    /// </summary>
    /// <param name="level">The level of the event that reports the duration.</param>
    /// <param name="keywords">The keywords of that event.</param>
    /// <returns>The timestamp, or 0.</returns>
    [NonEvent]
    public long StartTimer(EventLevel level, EventKeywords keywords)
        => IsEnabled(level, keywords) ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// The exception filter of a traced member: records what its core threw and returns false, so
    /// nothing is caught. The member writes the failure from the <c>finally</c> of the same
    /// <c>try</c>, which runs once the core's own <c>finally</c> blocks released what they held; a
    /// filter runs before them, while a core that threw inside a lock still holds it.
    /// </summary>
    /// <param name="exception">What the core threw.</param>
    /// <param name="captured">Receives <paramref name="exception"/>.</param>
    /// <returns>False, always.</returns>
    public static bool CaptureFailure(Exception exception, out Exception captured)
    {
        captured = exception;
        return false;
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
                GetTypeName(exception),
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
                GetTypeName(failure),
                failure.Message);
        }
    }

    /// <summary>
    /// Writes that an engine was constructed, with the worker failure window and minimum it gives up
    /// on a database with.
    /// </summary>
    /// <param name="engine">The engine; its base constructor has set every value read here.</param>
    [NonEvent]
    public void EngineCreated(DatabaseEngine engine)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            EngineCreated(engine.Name, engine.Model.ToString(), engine.WorkerFailureWindow.TotalMilliseconds, engine.WorkerFailureMinimumPasses);
        }
    }

    /// <summary>
    /// Writes that an engine's composition was frozen, with the workers and servers attached to it.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="workerCount">The workers attached.</param>
    /// <param name="serverCount">The servers attached.</param>
    [NonEvent]
    public void EngineComposed(DatabaseEngine engine, int workerCount, int serverCount)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            EngineComposed(engine.Name, engine.Model.ToString(), workerCount, serverCount);
        }
    }

    /// <summary>
    /// Writes the start of an engine's disposal, once, after its once-only exchange.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <returns>The timestamp <see cref="EngineDisposeStop"/> times the disposal from, or 0 when nothing was written.</returns>
    [NonEvent]
    public long EngineDisposeStart(DatabaseEngine engine)
    {
        if (!IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            return 0;
        }

        long started = Stopwatch.GetTimestamp();
        EngineDisposeStart(engine.Name, engine.Model.ToString());
        return started;
    }

    /// <summary>
    /// Writes the end of an engine's disposal that <see cref="EngineDisposeStart"/> reported.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="failureCount">How many components failed to close.</param>
    /// <param name="startTimestamp">What <see cref="EngineDisposeStart"/> returned.</param>
    [NonEvent]
    public void EngineDisposeStop(DatabaseEngine engine, int failureCount, long startTimestamp)
    {
        if (startTimestamp != 0 && IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            EngineDisposeStop(engine.Name, engine.Model.ToString(), failureCount, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that one or more components of an engine failed to close during its disposal.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="failureCount">How many components failed to close.</param>
    /// <param name="first">The first failure collected.</param>
    [NonEvent]
    public void EngineDisposeFailed(DatabaseEngine engine, int failureCount, Exception first)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            EngineDisposeFailed(engine.Name, engine.Model.ToString(), failureCount, GetTypeName(first), first.Message);
        }
    }

    /// <summary>
    /// Writes that a worker's <see cref="DatabaseEngineWorker.Run"/> returned before its engine stopped
    /// it, or threw: the engine reports <see cref="EngineState.Faulted"/> until it is disposed, and its
    /// pump runs the worker again after the backoff.
    /// </summary>
    /// <param name="engine">The engine whose pump ran the worker.</param>
    /// <param name="worker">The worker.</param>
    /// <param name="exception">What escaped, or the failure the engine records for an early return.</param>
    [NonEvent]
    public void WorkerLoopFaulted(DatabaseEngine engine, DatabaseEngineWorker worker, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            WorkerLoopFaulted(engine.Name, worker.Name, worker.Kind.ToString(), GetTypeName(exception), exception.Message);
        }
    }

    /// <summary>
    /// Writes that an engine created a database.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="startTimestamp">When the create started.</param>
    [NonEvent]
    public void DatabaseCreated(DatabaseEngine engine, DatabaseName database, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            DatabaseCreated(engine.Name, engine.Model.ToString(), database.ToString(), Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that an engine opened a database it held no open instance of before the call.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="waitedForClose">Whether the open waited for a holder's close of the database to end.</param>
    /// <param name="startTimestamp">When the open started.</param>
    [NonEvent]
    public void DatabaseOpened(DatabaseEngine engine, DatabaseName database, bool waitedForClose, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            DatabaseOpened(engine.Name, engine.Model.ToString(), database.ToString(), waitedForClose, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that an engine dropped a database.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    [NonEvent]
    public void DatabaseDropped(DatabaseEngine engine, DatabaseName database)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            DatabaseDropped(engine.Name, engine.Model.ToString(), database.ToString());
        }
    }

    /// <summary>
    /// Writes that a database's first close ended, whoever closed it: a holder, a drop, a reopen
    /// after it went offline, or the engine's disposal.
    /// </summary>
    /// <param name="engine">The database's engine.</param>
    /// <param name="database">The database's name.</param>
    [NonEvent]
    public void DatabaseClosed(DatabaseEngine engine, DatabaseName database)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            DatabaseClosed(engine.Name, engine.Model.ToString(), database.ToString());
        }
    }

    /// <summary>
    /// Writes that a create, open or drop of a database failed.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="operation"><c>Create</c>, <c>Open</c> or <c>Drop</c>.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void DatabaseOperationFailed(DatabaseEngine engine, DatabaseName database, string operation, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            DatabaseOperationFailed(engine.Name, engine.Model.ToString(), database.ToString(), operation, GetTypeName(exception), exception.Message);
        }
    }

    /// <summary>
    /// Writes the start of a worker's pass.
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <param name="pass">The pass's number, counted from one over the worker's life.</param>
    /// <returns>The timestamp <see cref="WorkerPassStop"/> times the pass from, or 0 when nothing was written.</returns>
    [NonEvent]
    public long WorkerPassStart(DatabaseEngineWorker worker, long pass)
    {
        if (!IsEnabled(EventLevel.Verbose, Keywords.Workers))
        {
            return 0;
        }

        long started = Stopwatch.GetTimestamp();
        WorkerPassStart(worker.Name, worker.Kind.ToString(), pass);
        return started;
    }

    /// <summary>
    /// Writes the end of a worker's pass that <see cref="WorkerPassStart"/> reported.
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <param name="pass">The pass's number.</param>
    /// <param name="failed">Whether the pass threw or reported a failure.</param>
    /// <param name="startTimestamp">What <see cref="WorkerPassStart"/> returned.</param>
    [NonEvent]
    public void WorkerPassStop(DatabaseEngineWorker worker, long pass, bool failed, long startTimestamp)
    {
        if (startTimestamp != 0 && IsEnabled(EventLevel.Verbose, Keywords.Workers))
        {
            WorkerPassStop(worker.Name, worker.Kind.ToString(), pass, failed, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that a worker's pass left work of a database for a later pass without a failure: a
    /// storage busy with a transaction, a checkpoint deferred or still running on its lane (owner
    /// decision 45).
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <param name="database">The database's name.</param>
    [NonEvent]
    public void WorkerDatabaseUnfinished(DatabaseEngineWorker worker, string database)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Workers))
        {
            WorkerDatabaseUnfinished(worker.Name, worker.Kind.ToString(), database);
        }
    }

    /// <summary>
    /// Writes that the engine's leaf could not take a database offline for a worker that gave up on
    /// it (its <c>TakeDatabaseOfflineCore</c> threw).
    /// </summary>
    /// <param name="worker">The worker that asked.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="exception">The leaf's failure.</param>
    [NonEvent]
    public void WorkerGiveUpFailed(DatabaseEngineWorker worker, string database, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            WorkerGiveUpFailed(worker.Name, worker.Kind.ToString(), database, GetTypeName(exception), exception.Message);
        }
    }

    /// <summary>
    /// Writes that a server started accepting.
    /// </summary>
    /// <param name="server">The server.</param>
    [NonEvent]
    public void ServerStarted(DatabaseServer server)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            var engine = server.Engine;
            ServerStarted(engine.Name, engine.Model.ToString(), GetServerType(server));
        }
    }

    /// <summary>
    /// Writes that a server's start failed, which leaves it stopped for good.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void ServerStartFailed(DatabaseServer server, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            var engine = server.Engine;
            ServerStartFailed(engine.Name, engine.Model.ToString(), GetServerType(server), GetTypeName(exception), exception.Message);
        }
    }

    /// <summary>
    /// Writes that a running server stopped and drained its sessions.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="startTimestamp">When the stop started, from <see cref="StartTimer"/>; 0 when it was not timed.</param>
    [NonEvent]
    public void ServerStopped(DatabaseServer server, long startTimestamp)
    {
        if (startTimestamp != 0 && IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            var engine = server.Engine;
            ServerStopped(engine.Name, engine.Model.ToString(), GetServerType(server), Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes the protocol version a server session's handshake negotiated.
    /// </summary>
    /// <param name="session">The server session.</param>
    /// <param name="version">The negotiated version.</param>
    [NonEvent]
    public void ServerSessionNegotiated(DatabaseServerSession session, ProtocolVersion version)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Sessions))
        {
            ServerSessionNegotiated(session.Id, version.ToString());
        }
    }

    /// <summary>
    /// Writes the principal a server session's authentication accepted. The principal's name is an
    /// identifier (rule 11); the evidence is never written.
    /// </summary>
    /// <param name="session">The server session.</param>
    /// <param name="principal">The authenticated principal's name.</param>
    [NonEvent]
    public void ServerSessionAuthenticated(DatabaseServerSession session, string principal)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ServerSessionAuthenticated(session.Id, principal);
        }
    }

    /// <summary>
    /// Counts a new session into <c>current-sessions</c> and writes that it opened. Called once per
    /// session, by its base constructor.
    /// </summary>
    /// <param name="session">The session.</param>
    [NonEvent]
    public void SessionOpened(DatabaseSession session)
    {
        Interlocked.Increment(ref _currentSessions);
        if (IsEnabled(EventLevel.Verbose, Keywords.Sessions))
        {
            var database = session.Database;
            SessionOpened(database.Engine.Name, database.Name.ToString(), session.SessionNumber);
        }
    }

    /// <summary>
    /// Counts a session out of <c>current-sessions</c> at its one close transition.
    /// </summary>
    [NonEvent]
    public void SessionClosing() => Interlocked.Decrement(ref _currentSessions);

    /// <summary>
    /// Writes that a session's close ended.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="failed">Whether the leaf's teardown or the transaction's rollback failed.</param>
    [NonEvent]
    public void SessionClosed(DatabaseSession session, bool failed)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Sessions))
        {
            SessionClosed(session.Database.Name.ToString(), session.SessionNumber, failed);
        }
    }

    /// <summary>
    /// Writes the start of the outermost statement on a session.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="request">The typed request, or null for statement text.</param>
    [NonEvent]
    public void StatementStart(DatabaseSession session, QueryRequest? request)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Statements))
        {
            StatementStart(session.Database.Name.ToString(), session.SessionNumber, GetRequestKind(request));
        }
    }

    /// <summary>
    /// Writes the end of a statement whose core returned a result: <c>StatementFailed</c> when the
    /// result's status is <see cref="QueryResultStatus.Error"/>, <c>SlowStatement</c> when it ran at
    /// least the threshold, and <c>StatementStop</c>.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="request">The typed request, or null for statement text.</param>
    /// <param name="result">The result the core returned.</param>
    /// <param name="startTimestamp">When the statement started.</param>
    [NonEvent]
    public void StatementCompleted(DatabaseSession session, QueryRequest? request, QueryResult? result, long startTimestamp)
    {
        // A null result is the leaf's contract violation; its caller receives it as it is, and the
        // trace reads it as a success with no count.
        var status = result?.Status ?? QueryResultStatus.Success;
        double duration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (status == QueryResultStatus.Error && IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            // The code only: a diagnostic's message can quote the statement (owner question Q3).
            var diagnostic = FindFailureDiagnostic(result!.Diagnostics);
            StatementFailed(
                session.Database.Name.ToString(),
                session.SessionNumber,
                GetRequestKind(request),
                diagnostic?.Code ?? nameof(QueryResultStatus.Error),
                duration);
        }

        WriteStatementEnd(session, request, status, result?.AffectedCount ?? -1, duration);
    }

    /// <summary>
    /// Writes the end of a statement whose core threw: <c>StatementFailed</c> unless it was
    /// cancelled, <c>SlowStatement</c> when it ran at least the threshold, and <c>StatementStop</c>.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="request">The typed request, or null for statement text.</param>
    /// <param name="exception">What the core threw.</param>
    /// <param name="startTimestamp">When the statement started.</param>
    [NonEvent]
    public void StatementThrew(DatabaseSession session, QueryRequest? request, Exception exception, long startTimestamp)
    {
        double duration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var status = QueryResultStatus.Cancelled;
        if (exception is not OperationCanceledException)
        {
            status = QueryResultStatus.Error;
            if (IsEnabled(EventLevel.Error, EventKeywords.None))
            {
                // The type only: a parse error quotes the token it stopped at (owner question Q3).
                StatementFailed(
                    session.Database.Name.ToString(),
                    session.SessionNumber,
                    GetRequestKind(request),
                    GetTypeName(exception),
                    duration);
            }
        }

        WriteStatementEnd(session, request, status, -1, duration);
    }

    /// <summary>
    /// Writes that a session's explicit transaction began and became the session's.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="transaction">The transaction.</param>
    [NonEvent]
    public void TransactionBegun(DatabaseSession session, DatabaseTransaction transaction)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Transactions))
        {
            TransactionBegun(session.Database.Name.ToString(), session.SessionNumber, transaction.Id.ToString(), transaction.IsolationLevel.ToString());
        }
    }

    /// <summary>
    /// Writes that an explicit transaction committed.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="startTimestamp">When the commit started, from <see cref="StartTimer"/>; 0 when it was not timed.</param>
    [NonEvent]
    public void TransactionCommitted(DatabaseTransaction transaction, long startTimestamp)
    {
        if (startTimestamp != 0 && IsEnabled(EventLevel.Verbose, Keywords.Transactions))
        {
            TransactionCommitted(transaction.Id.ToString(), Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that an explicit transaction's kernel transaction was rolled back by its caller's
    /// rollback, its disposal, or its session's close.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cause"><c>Rollback</c>, <c>Dispose</c> or <c>SessionClosed</c>.</param>
    [NonEvent]
    public void TransactionRolledBack(DatabaseTransaction transaction, string cause)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Transactions))
        {
            TransactionRolledBack(transaction.Id.ToString(), cause);
        }
    }

    /// <summary>
    /// Writes that an operation's failure aborted an explicit transaction, which now refuses work
    /// until its caller rolls it back. The operation's own failure is <c>StatementFailed</c>; the
    /// cause is written by its type only, since its message can quote the statement (owner question
    /// Q3).
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cause">The operation's failure.</param>
    [NonEvent]
    public void TransactionAborted(DatabaseTransaction transaction, Exception cause)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Transactions))
        {
            TransactionAborted(transaction.Id.ToString(), GetTypeName(cause));
        }
    }

    /// <summary>
    /// Writes that an explicit transaction's commit failed: refused (offline, already ended, an
    /// operation still running), turned into a rollback because an operation aborted the transaction,
    /// or failed in the kernel.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="aborted">
    /// Whether an operation's failure had aborted the transaction. Every model's refusal of such a
    /// commit repeats the operation's message, which can quote the statement, so the message is
    /// written empty (owner question Q3).
    /// </param>
    [NonEvent]
    public void TransactionCommitFailed(DatabaseTransaction transaction, Exception exception, bool aborted)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            TransactionCommitFailed(transaction.Id.ToString(), GetTypeName(exception), aborted ? string.Empty : exception.Message);
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

    [Event(4, Level = EventLevel.Informational, Message = "Database engine {0} ({1}) created; it gives up on a database once a worker's failures on it last {2} ms across {3} failed passes.")]
    private void EngineCreated(string engineName, string model, double workerFailureWindowMilliseconds, int workerFailureMinimumPasses)
        => WriteEvent(4, engineName, model, workerFailureWindowMilliseconds, workerFailureMinimumPasses);

    [Event(5, Level = EventLevel.Informational, Message = "Database engine {0} ({1}) composed with {2} worker(s) and {3} server(s).")]
    private void EngineComposed(string engineName, string model, int workerCount, int serverCount)
        => WriteEvent(5, engineName, model, workerCount, serverCount);

    [Event(6, Level = EventLevel.Informational, Message = "Database engine {0} ({1}) is disposing its servers, workers and databases.")]
    private void EngineDisposeStart(string engineName, string model)
        => WriteEvent(6, engineName, model);

    [Event(7, Level = EventLevel.Informational, Message = "Database engine {0} ({1}) disposed in {3} ms; {2} component(s) failed to close.")]
    private void EngineDisposeStop(string engineName, string model, int failureCount, double durationMilliseconds)
        => WriteEvent(7, engineName, model, failureCount, durationMilliseconds);

    [Event(8, Level = EventLevel.Error, Message = "Database engine {0}'s worker {1} ({2}) left its loop before the engine stopped it: {3}: {4}. The engine reports Faulted until it is disposed and runs the worker again.")]
    private void WorkerLoopFaulted(string engineName, string workerName, string workerKind, string exceptionType, string exceptionMessage)
        => WriteEvent(8, engineName, workerName, workerKind, exceptionType, exceptionMessage);

    [Event(9, Level = EventLevel.Informational, Message = "Database engine {0} ({1}) created database '{2}' in {3} ms.")]
    private void DatabaseCreated(string engineName, string model, string database, double durationMilliseconds)
        => WriteEvent(9, engineName, model, database, durationMilliseconds);

    [Event(10, Level = EventLevel.Informational, Message = "Database engine {0} ({1}) opened database '{2}' in {4} ms; waited for a close: {3}.")]
    private void DatabaseOpened(string engineName, string model, string database, bool waitedForClose, double durationMilliseconds)
        => WriteEvent(10, engineName, model, database, waitedForClose, durationMilliseconds);

    [Event(11, Level = EventLevel.Informational, Message = "Database engine {0} ({1}) dropped database '{2}'.")]
    private void DatabaseDropped(string engineName, string model, string database)
        => WriteEvent(11, engineName, model, database);

    [Event(12, Level = EventLevel.Informational, Message = "Database '{2}' of database engine {0} ({1}) closed.")]
    private void DatabaseClosed(string engineName, string model, string database)
        => WriteEvent(12, engineName, model, database);

    [Event(13, Level = EventLevel.Error, Message = "Database engine {0} ({1}) failed to {3} database '{2}': {4}: {5}")]
    private void DatabaseOperationFailed(string engineName, string model, string database, string operation, string exceptionType, string exceptionMessage)
        => WriteEvent(13, engineName, model, database, operation, exceptionType, exceptionMessage);

    [Event(14, Level = EventLevel.Verbose, Keywords = Keywords.Workers, Message = "Database engine worker {0} ({1}) started pass {2}.")]
    private void WorkerPassStart(string workerName, string workerKind, long pass)
        => WriteEvent(14, workerName, workerKind, pass);

    [Event(15, Level = EventLevel.Verbose, Keywords = Keywords.Workers, Message = "Database engine worker {0} ({1}) ended pass {2} in {4} ms; failed: {3}.")]
    private void WorkerPassStop(string workerName, string workerKind, long pass, bool failed, double durationMilliseconds)
        => WriteEvent(15, workerName, workerKind, pass, failed, durationMilliseconds);

    [Event(16, Level = EventLevel.Verbose, Keywords = Keywords.Workers, Message = "Database engine worker {0} ({1}) left work of database '{2}' for a later pass.")]
    private void WorkerDatabaseUnfinished(string workerName, string workerKind, string database)
        => WriteEvent(16, workerName, workerKind, database);

    [Event(17, Level = EventLevel.Error, Message = "The engine could not take database '{2}' offline for its worker {0} ({1}): {3}: {4}. The worker holds the failure, and the database's next failure asks again.")]
    private void WorkerGiveUpFailed(string workerName, string workerKind, string database, string exceptionType, string exceptionMessage)
        => WriteEvent(17, workerName, workerKind, database, exceptionType, exceptionMessage);

    [Event(18, Level = EventLevel.Informational, Message = "Database server {2} of engine {0} ({1}) started.")]
    private void ServerStarted(string engineName, string model, string serverType)
        => WriteEvent(18, engineName, model, serverType);

    [Event(19, Level = EventLevel.Error, Message = "Database server {2} of engine {0} ({1}) failed to start: {3}: {4}. The server is stopped for good.")]
    private void ServerStartFailed(string engineName, string model, string serverType, string exceptionType, string exceptionMessage)
        => WriteEvent(19, engineName, model, serverType, exceptionType, exceptionMessage);

    [Event(20, Level = EventLevel.Informational, Message = "Database server {2} of engine {0} ({1}) stopped in {3} ms.")]
    private void ServerStopped(string engineName, string model, string serverType, double durationMilliseconds)
        => WriteEvent(20, engineName, model, serverType, durationMilliseconds);

    [Event(21, Level = EventLevel.Verbose, Keywords = Keywords.Sessions, Message = "Server session {0} negotiated protocol version {1}.")]
    private void ServerSessionNegotiated(Guid sessionId, string protocolVersion)
        => WriteEvent(21, sessionId, protocolVersion);

    [Event(22, Level = EventLevel.Informational, Message = "Server session {0} authenticated principal '{1}'.")]
    private void ServerSessionAuthenticated(Guid sessionId, string principal)
        => WriteEvent(22, sessionId, principal);

    [Event(23, Level = EventLevel.Verbose, Keywords = Keywords.Sessions, Message = "Session {2} opened on database '{1}' of engine {0}.")]
    private void SessionOpened(string engineName, string database, long sessionNumber)
        => WriteEvent(23, engineName, database, sessionNumber);

    [Event(24, Level = EventLevel.Verbose, Keywords = Keywords.Sessions, Message = "Session {1} on database '{0}' closed; failed: {2}.")]
    private void SessionClosed(string database, long sessionNumber, bool failed)
        => WriteEvent(24, database, sessionNumber, failed);

    [Event(25, Level = EventLevel.Verbose, Keywords = Keywords.Statements, Message = "Session {1} on database '{0}' started a {2} statement.")]
    private void StatementStart(string database, long sessionNumber, string requestKind)
        => WriteEvent(25, database, sessionNumber, requestKind);

    [Event(26, Level = EventLevel.Verbose, Keywords = Keywords.Statements, Message = "Session {1} on database '{0}' ended a statement {2} in {4} ms; affected: {3}.")]
    private void StatementStop(string database, long sessionNumber, string status, long affectedCount, double durationMilliseconds)
        => WriteEvent(26, database, sessionNumber, status, affectedCount, durationMilliseconds);

    [Event(27, Level = EventLevel.Warning, Message = "A {4} statement of session {3} on database '{2}' of engine {0} ({1}) ended {5} after {6} ms, at least the slow-statement threshold of {7} ms.")]
    private void SlowStatement(string engineName, string model, string database, long sessionNumber, string requestKind, string status, double durationMilliseconds, double thresholdMilliseconds)
        => WriteEvent(27, engineName, model, database, sessionNumber, requestKind, status, durationMilliseconds, thresholdMilliseconds);

    [Event(28, Level = EventLevel.Error, Message = "A {2} statement of session {1} on database '{0}' failed after {4} ms: {3}.")]
    private void StatementFailed(string database, long sessionNumber, string requestKind, string failure, double durationMilliseconds)
        => WriteEvent(28, database, sessionNumber, requestKind, failure, durationMilliseconds);

    [Event(29, Level = EventLevel.Verbose, Keywords = Keywords.Transactions, Message = "Session {1} on database '{0}' began transaction {2} at {3}.")]
    private void TransactionBegun(string database, long sessionNumber, string transactionId, string isolationLevel)
        => WriteEvent(29, database, sessionNumber, transactionId, isolationLevel);

    [Event(30, Level = EventLevel.Verbose, Keywords = Keywords.Transactions, Message = "Transaction {0} committed in {1} ms.")]
    private void TransactionCommitted(string transactionId, double durationMilliseconds)
        => WriteEvent(30, transactionId, durationMilliseconds);

    [Event(31, Level = EventLevel.Verbose, Keywords = Keywords.Transactions, Message = "Transaction {0} rolled back ({1}).")]
    private void TransactionRolledBack(string transactionId, string cause)
        => WriteEvent(31, transactionId, cause);

    [Event(32, Level = EventLevel.Verbose, Keywords = Keywords.Transactions, Message = "Transaction {0} aborted because an operation in it failed with {1}. It refuses work until its caller rolls it back.")]
    private void TransactionAborted(string transactionId, string exceptionType)
        => WriteEvent(32, transactionId, exceptionType);

    [Event(33, Level = EventLevel.Error, Message = "Transaction {0} failed to commit: {1}: {2}")]
    private void TransactionCommitFailed(string transactionId, string exceptionType, string exceptionMessage)
        => WriteEvent(33, transactionId, exceptionType, exceptionMessage);

    [Event(34, Level = EventLevel.Error, Message = "Database engine {0} ({1}) disposed with {2} component(s) that failed to close; the first failure was {3}: {4}")]
    private void EngineDisposeFailed(string engineName, string model, int failureCount, string exceptionType, string exceptionMessage)
        => WriteEvent(34, engineName, model, failureCount, exceptionType, exceptionMessage);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Disable)
        {
            // A session that ends takes its threshold with it: a brief tool session that set 0 ms
            // must not leave a forwarder that is still enabled reporting every statement as slow.
            Volatile.Write(ref _slowStatementThresholdMilliseconds, DefaultSlowStatementThresholdMilliseconds);
            return;
        }

        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        // Each enabling session sets the threshold (plan D7): to its argument, or to the default when
        // it passes none or one that does not parse as a finite, non-negative number.
        double threshold = DefaultSlowStatementThresholdMilliseconds;
        if (command.Arguments is { } arguments
            && arguments.TryGetValue(SlowStatementThresholdArgument, out string? value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
            && parsed >= 0)
        {
            threshold = parsed;
        }

        Volatile.Write(ref _slowStatementThresholdMilliseconds, threshold);

        // Created on the first enable command, as the runtime's own sources do, and kept for the
        // source's lifetime. The backing field is maintained whether or not anyone listens, so a tool
        // that attaches late still reads the exact value.
        _currentSessionsCounter ??= new PollingCounter("current-sessions", this, () => Interlocked.Read(ref _currentSessions))
        {
            DisplayName = "Current Sessions",
        };
    }

    [NonEvent]
    private void WriteStatementEnd(DatabaseSession session, QueryRequest? request, QueryResultStatus status, long affectedCount, double duration)
    {
        double threshold = Volatile.Read(ref _slowStatementThresholdMilliseconds);
        if (duration >= threshold && IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            var database = session.Database;
            var engine = database.Engine;
            SlowStatement(
                engine.Name,
                engine.Model.ToString(),
                database.Name.ToString(),
                session.SessionNumber,
                GetRequestKind(request),
                status.ToString(),
                duration,
                threshold);
        }

        // Last, so the statement's activity closes after the events it holds.
        if (IsEnabled(EventLevel.Verbose, Keywords.Statements))
        {
            StatementStop(session.Database.Name.ToString(), session.SessionNumber, status.ToString(), affectedCount, duration);
        }
    }

    // The diagnostic a failed result reports first: its first error, or its first diagnostic of any
    // severity when it holds no error.
    private static Diagnostic? FindFailureDiagnostic(IReadOnlyList<Diagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return null;
        }

        for (int index = 0; index < diagnostics.Count; index++)
        {
            if (diagnostics[index] is { Severity: DiagnosticSeverity.Error } error)
            {
                return error;
            }
        }

        return diagnostics[0];
    }

    private static string GetRequestKind(QueryRequest? request)
        => request is null ? TextRequestKind : request.GetType().Name;

    private static string GetServerType(DatabaseServer server) => server.GetType().Name;

    private static string GetTypeName(Exception exception)
        => exception.GetType().FullName ?? exception.GetType().Name;

    /// <summary>
    /// The source's keywords: each family of high-volume <see cref="EventLevel.Verbose"/> events has
    /// one, so a tool can take one family without the rest (event-sources plan D5 d).
    /// </summary>
    public static class Keywords
    {
        /// <summary>Worker passes and the work a pass left unfinished.</summary>
        public const EventKeywords Workers = (EventKeywords)0x1;

        /// <summary>Sessions opened and closed, and server-session handshakes.</summary>
        public const EventKeywords Sessions = (EventKeywords)0x2;

        /// <summary>Statement start and stop.</summary>
        public const EventKeywords Statements = (EventKeywords)0x4;

        /// <summary>Explicit transactions begun, committed, rolled back and aborted.</summary>
        public const EventKeywords Transactions = (EventKeywords)0x8;
    }
}
