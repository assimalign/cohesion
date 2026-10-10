using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// Serializes the tests that observe the Database event source: the source is process-wide, so the
/// collection runs alone, after every parallel collection.
/// </summary>
[CollectionDefinition(nameof(DatabaseEventSourceCollection), DisableParallelization = true)]
public class DatabaseEventSourceCollection
{
}

/// <summary>
/// The Database root's event source against the repository's EventSource convention: its name and
/// manifest, the engine, database, server, session, statement and explicit-transaction lifecycle it
/// reports over real in-memory engines and the root's test doubles, the worker failures and
/// recoveries it reports (#1268), its <c>current-sessions</c> counter, and what the statement path
/// allocates while nobody listens (the event-sources plan, §7).
/// </summary>
[Collection(nameof(DatabaseEventSourceCollection))]
public sealed class DatabaseEventSourceTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should be named for its assembly")]
    public void GetName_DatabaseEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(DatabaseEventSource));

        // Assert
        name.ShouldBe(typeof(DatabaseEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(DatabaseEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database", Case.Sensitive);
        for (int id = 1; id <= 34; id++)
        {
            manifest.ShouldContain($"value=\"{id}\"", Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report each worker failure and each recovery once with its declared payload")]
    public void RunIteration_FailuresThenRecovery_ShouldReportEachOnce()
    {
        // Arrange: database a fails on passes 1 and 2 and recovers on pass 3; pass 4 throws as a
        // whole, and pass 5 runs to its end.
        string name = "event-source-" + Guid.NewGuid().ToString("N");
        var worker = new ScriptedWorker((self, pass) =>
        {
            if (pass == 4)
            {
                throw new InvalidOperationException("the pass failed");
            }

            self.Begin("a").ShouldBeTrue();
            if (pass <= 2)
            {
                self.Fail("a", new InvalidOperationException($"database a failed on pass {pass}"), TimeSpan.Zero);
            }
        }, name: name);
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational);

        // Act
        for (int pass = 1; pass <= 5; pass++)
        {
            worker.RunIteration(CancellationToken.None);
        }

        // Assert
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], name)).ToArray();
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        events.Select(e => (e.EventName, Database: (string)e.Payload![2]!)).ShouldBe(
        [
            ("WorkerFailed", "a"),
            ("WorkerFailed", "a"),
            ("WorkerRecovered", "a"),
            ("WorkerFailed", string.Empty),
            ("WorkerRecovered", string.Empty),
        ]);

        var failed = events[1];
        failed.EventId.ShouldBe(1);
        failed.Level.ShouldBe(EventLevel.Warning);
        failed.PayloadNames.ShouldBe(["workerName", "workerKind", "database", "exceptionType", "exceptionMessage", "consecutiveFailures"]);
        failed.Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), "a", typeof(InvalidOperationException).FullName, "database a failed on pass 2", 2]);

        var recovered = events[2];
        recovered.EventId.ShouldBe(2);
        recovered.Level.ShouldBe(EventLevel.Informational);
        recovered.PayloadNames.ShouldBe(["workerName", "workerKind", "database", "failures"]);
        recovered.Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), "a", 2]);

        events[3].Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), string.Empty, typeof(InvalidOperationException).FullName, "the pass failed", 1]);
        events[4].Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), string.Empty, 1]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a database its engine took offline once, with its declared payload")]
    public async Task RunIteration_FailuresOutlastTheWindow_ShouldReportDatabaseTakenOfflineOnce()
    {
        // Arrange: an engine that gives up after two failed passes a second apart, and a checkpoint
        // worker whose passes keep failing on database a (owner decisions 25 and 42).
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        var clock = new ManualTimeProvider();
        await using var engine = new TestEngine(engineName, workerFailureWindow: TimeSpan.FromSeconds(1), workerFailureMinimumPasses: 2, time: clock);
        await engine.CreateDatabaseAsync("a");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a");
            self.Fail("a", new InvalidOperationException($"checkpoint failed on pass {pass}"), TimeSpan.Zero);
        }, name: engineName + "/checkpoint");
        engine.Attach(worker);
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational);

        // Act: the second pass, a second after the first, gives up on a; the engine takes it offline
        // on a thread-pool thread and writes the event there. The third pass starts a new streak, or
        // finds the give-up still running; either way it takes nothing offline.
        for (int pass = 1; pass <= 3; pass++)
        {
            worker.RunIteration(CancellationToken.None);
            if (pass == 1)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        await recorder.WaitForAsync(e => e.EventId == 3 && Equals(e.Payload?[0], engineName));
        await Task.Delay(100);

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var offline = recorder.Events.Where(e => e.EventId == 3 && Equals(e.Payload?[0], engineName)).ShouldHaveSingleItem();
        offline.EventName.ShouldBe("DatabaseTakenOffline");
        offline.Level.ShouldBe(EventLevel.Error);
        offline.PayloadNames.ShouldBe(["engineName", "database", "cause", "workerName", "workerKind", "exceptionType", "exceptionMessage"]);
        offline.Payload.ShouldBe([engineName, "a", "CheckpointFailures", engineName + "/checkpoint", nameof(DatabaseEngineWorkerKind.Checkpoint),
            typeof(InvalidOperationException).FullName, "checkpoint failed on pass 2"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should write nothing below its enabled level")]
    public void WorkerRecovered_WarningListener_ShouldNotBeWritten()
    {
        // Arrange
        string name = "event-source-" + Guid.NewGuid().ToString("N");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a").ShouldBeTrue();
            if (pass == 1)
            {
                self.Fail("a", new InvalidOperationException("failed"), TimeSpan.Zero);
            }
        }, name: name);
        using var recorder = new DatabaseEventRecorder(EventLevel.Warning);

        // Act
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);

        // Assert: the failure (a warning) only.
        recorder.Events.Where(e => Equals(e.Payload?[0], name)).Select(e => e.EventName).ShouldBe(["WorkerFailed"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a real engine's database lifecycle once per transition")]
    public async Task DatabaseLifecycle_RealSqlEngine_ShouldReportEachTransitionOnce()
    {
        // Arrange
        string engineName = "event-source-sql-" + Guid.NewGuid().ToString("N");
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational);
        Exception? duplicate;
        Exception? missing;

        // Act: create, close, reopen, open again while open, a missing open, a duplicate create, a
        // drop, and the engine's disposal.
        await using (var engine = SqlDatabaseEngine.Create(engineName, new SqlDatabaseEngineOptions()))
        {
            var created = await engine.CreateDatabaseAsync("lifecycle");
            await created.DisposeAsync();
            var opened = await engine.OpenDatabaseAsync("lifecycle");
            (await engine.OpenDatabaseAsync("lifecycle")).ShouldBeSameAs(opened);
            missing = await Record.ExceptionAsync(async () => await engine.OpenDatabaseAsync("missing"));
            duplicate = await Record.ExceptionAsync(async () => await engine.CreateDatabaseAsync("lifecycle"));
            await engine.DropDatabaseAsync("lifecycle");
        }

        // Assert
        missing.ShouldBeOfType<DatabaseNotFoundException>();
        duplicate.ShouldNotBeNull();
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(Payload(e, "engineName"), engineName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(
        [
            "EngineCreated",
            "EngineComposed",
            "DatabaseCreated",
            "DatabaseClosed",
            "DatabaseOpened",
            "DatabaseOperationFailed",
            "DatabaseClosed",
            "DatabaseDropped",
            "EngineDisposeStart",
            "EngineDisposeStop",
        ]);

        var engineCreated = events[0];
        engineCreated.EventId.ShouldBe(4);
        engineCreated.Level.ShouldBe(EventLevel.Informational);
        engineCreated.PayloadNames.ShouldBe(["engineName", "model", "workerFailureWindowMilliseconds", "workerFailureMinimumPasses"]);
        engineCreated.Payload.ShouldBe([engineName, nameof(EngineModel.Sql), DatabaseEngine.DefaultWorkerFailureWindow.TotalMilliseconds, DatabaseEngine.DefaultWorkerFailureMinimumPasses]);

        var composed = events[1];
        composed.EventId.ShouldBe(5);
        composed.PayloadNames.ShouldBe(["engineName", "model", "workerCount", "serverCount"]);
        ((int)Payload(composed, "workerCount")!).ShouldBeGreaterThan(0);
        Payload(composed, "serverCount").ShouldBe(0);

        var databaseCreated = events[2];
        databaseCreated.EventId.ShouldBe(9);
        databaseCreated.PayloadNames.ShouldBe(["engineName", "model", "database", "durationMilliseconds"]);
        Payload(databaseCreated, "database").ShouldBe("lifecycle");
        ((double)Payload(databaseCreated, "durationMilliseconds")!).ShouldBeGreaterThanOrEqualTo(0);

        var closed = events[3];
        closed.EventId.ShouldBe(12);
        closed.PayloadNames.ShouldBe(["engineName", "model", "database"]);
        closed.Payload.ShouldBe([engineName, nameof(EngineModel.Sql), "lifecycle"]);

        var databaseOpened = events[4];
        databaseOpened.EventId.ShouldBe(10);
        databaseOpened.PayloadNames.ShouldBe(["engineName", "model", "database", "waitedForClose", "durationMilliseconds"]);
        Payload(databaseOpened, "database").ShouldBe("lifecycle");
        Payload(databaseOpened, "waitedForClose").ShouldBe(false);

        var operationFailed = events[5];
        operationFailed.EventId.ShouldBe(13);
        operationFailed.Level.ShouldBe(EventLevel.Error);
        operationFailed.PayloadNames.ShouldBe(["engineName", "model", "database", "operation", "exceptionType", "exceptionMessage"]);
        operationFailed.Payload.ShouldBe([engineName, nameof(EngineModel.Sql), "lifecycle", "Create", duplicate.GetType().FullName, duplicate.Message]);

        var dropped = events[7];
        dropped.EventId.ShouldBe(11);
        dropped.PayloadNames.ShouldBe(["engineName", "model", "database"]);
        dropped.Payload.ShouldBe([engineName, nameof(EngineModel.Sql), "lifecycle"]);

        var disposeStart = events[8];
        disposeStart.EventId.ShouldBe(6);
        disposeStart.Opcode.ShouldBe(EventOpcode.Start);
        disposeStart.PayloadNames.ShouldBe(["engineName", "model"]);

        var disposeStop = events[9];
        disposeStop.EventId.ShouldBe(7);
        disposeStop.Opcode.ShouldBe(EventOpcode.Stop);
        disposeStop.PayloadNames.ShouldBe(["engineName", "model", "status", "failureCount", "durationMilliseconds"]);
        Payload(disposeStop, "status").ShouldBe(nameof(QueryResultStatus.Success));
        Payload(disposeStop, "failureCount").ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a real engine's statements and explicit transactions once each")]
    public async Task StatementsAndTransactions_RealSqlEngine_ShouldReportEachOnce()
    {
        // Arrange: a 0 ms threshold makes every statement slow (plan D7).
        string engineName = "event-source-sql-" + Guid.NewGuid().ToString("N");
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose, slowStatementThreshold: "0");
        await using var engine = SqlDatabaseEngine.Create(engineName, new SqlDatabaseEngineOptions());
        DatabaseInstance database = await engine.CreateDatabaseAsync("statements");
        var session = await database.CreateSessionAsync();
        long sessionNumber = session.SessionNumber;

        // Act: a statement that succeeds, one that fails to parse (thrown) and one the session
        // refuses with a coded result (COMMIT with no open transaction); a committed and a rolled
        // back transaction; a commit of the rolled back one; then the session's close.
        var succeeded = await session.ExecuteAsync("CREATE TABLE items (id INT NOT NULL)");
        var parseFailure = await Record.ExceptionAsync(async () => await session.ExecuteAsync("SELEC 1"));
        var failed = await session.ExecuteAsync("COMMIT");
        var committed = await session.BeginTransactionAsync();
        await session.ExecuteAsync("INSERT INTO items (id) VALUES (1)");
        await committed.CommitAsync();
        await committed.DisposeAsync();
        var rolledBack = await session.BeginTransactionAsync();
        await rolledBack.RollbackAsync();
        var refusal = await Record.ExceptionAsync(async () => await rolledBack.CommitAsync());
        await session.DisposeAsync();

        // Assert
        succeeded.Status.ShouldBe(QueryResultStatus.Success);
        parseFailure.ShouldBeOfType<DatabaseParseException>();
        failed.Status.ShouldBe(QueryResultStatus.Error);
        refusal.ShouldNotBeNull();
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        var sessionEvents = recorder.Events.Where(e => Equals(Payload(e, "sessionNumber"), sessionNumber)).ToArray();
        sessionEvents.Select(e => e.EventName).ShouldBe(
        [
            "SessionOpened",
            "StatementStart", "SlowStatement", "StatementStop",
            "StatementStart", "StatementFailed", "SlowStatement", "StatementStop",
            "StatementStart", "StatementFailed", "SlowStatement", "StatementStop",
            "TransactionBegun",
            "StatementStart", "SlowStatement", "StatementStop",
            "TransactionBegun",
            "SessionClosed",
        ]);

        var opened = sessionEvents[0];
        opened.EventId.ShouldBe(23);
        opened.Level.ShouldBe(EventLevel.Verbose);
        opened.Keywords.HasFlag(DatabaseEventSource.Keywords.Sessions).ShouldBeTrue();
        opened.PayloadNames.ShouldBe(["engineName", "database", "sessionNumber"]);
        opened.Payload.ShouldBe([engineName, "statements", sessionNumber]);

        var start = sessionEvents[1];
        start.EventId.ShouldBe(25);
        start.Opcode.ShouldBe(EventOpcode.Start);
        start.Keywords.HasFlag(DatabaseEventSource.Keywords.Statements).ShouldBeTrue();
        start.PayloadNames.ShouldBe(["database", "sessionNumber", "requestKind"]);
        start.Payload.ShouldBe(["statements", sessionNumber, DatabaseEventSource.TextRequestKind]);

        var slow = sessionEvents[2];
        slow.EventId.ShouldBe(27);
        slow.Level.ShouldBe(EventLevel.Warning);
        slow.PayloadNames.ShouldBe(["engineName", "model", "database", "sessionNumber", "requestKind", "status", "durationMilliseconds", "thresholdMilliseconds"]);
        Payload(slow, "engineName").ShouldBe(engineName);
        Payload(slow, "model").ShouldBe(nameof(EngineModel.Sql));
        Payload(slow, "status").ShouldBe(nameof(QueryResultStatus.Success));
        Payload(slow, "thresholdMilliseconds").ShouldBe(0d);

        var stop = sessionEvents[3];
        stop.EventId.ShouldBe(26);
        stop.Opcode.ShouldBe(EventOpcode.Stop);
        stop.PayloadNames.ShouldBe(["database", "sessionNumber", "status", "affectedCount", "durationMilliseconds"]);
        Payload(stop, "status").ShouldBe(nameof(QueryResultStatus.Success));
        Payload(stop, "affectedCount").ShouldBe(succeeded.AffectedCount);

        // The parse failure the session threw: its type, and not its message, which can quote the
        // statement (owner question Q3).
        var thrown = sessionEvents[5];
        thrown.EventId.ShouldBe(28);
        thrown.Level.ShouldBe(EventLevel.Error);
        thrown.PayloadNames.ShouldBe(["database", "sessionNumber", "requestKind", "code", "exceptionType", "durationMilliseconds"]);
        Payload(thrown, "exceptionType").ShouldBe(typeof(DatabaseParseException).FullName);
        Payload(thrown, "code").ShouldBe(string.Empty);
        Payload(thrown, "requestKind").ShouldBe(DatabaseEventSource.TextRequestKind);
        Payload(sessionEvents[7], "status").ShouldBe(nameof(QueryResultStatus.Error));
        Payload(sessionEvents[7], "affectedCount").ShouldBe(-1L);

        // The coded result the session returned: its error diagnostic's code.
        var coded = sessionEvents[9];
        var diagnostic = failed.Diagnostics!.First(d => d.Severity == DiagnosticSeverity.Error);
        coded.EventId.ShouldBe(28);
        diagnostic.Code.ShouldBe("COHSQLT002");
        Payload(coded, "code").ShouldBe(diagnostic.Code);
        Payload(coded, "exceptionType").ShouldBe(string.Empty);
        Payload(sessionEvents[11], "status").ShouldBe(nameof(QueryResultStatus.Error));

        var begun = sessionEvents[12];
        begun.EventId.ShouldBe(29);
        begun.Keywords.HasFlag(DatabaseEventSource.Keywords.Transactions).ShouldBeTrue();
        begun.PayloadNames.ShouldBe(["database", "sessionNumber", "transactionId", "isolationLevel"]);
        begun.Payload.ShouldBe(["statements", sessionNumber, committed.Id.ToString(), committed.IsolationLevel.ToString()]);

        var closed = sessionEvents[^1];
        closed.EventId.ShouldBe(24);
        closed.PayloadNames.ShouldBe(["database", "sessionNumber", "failed"]);
        closed.Payload.ShouldBe(["statements", sessionNumber, false]);

        // The committed transaction: one commit, and no rollback from its disposal.
        var committedEvents = recorder.Events.Where(e => Equals(Payload(e, "transactionId"), committed.Id.ToString())).ToArray();
        committedEvents.Select(e => e.EventName).ShouldBe(["TransactionBegun", "TransactionCommitted"]);
        committedEvents[1].EventId.ShouldBe(30);
        committedEvents[1].PayloadNames.ShouldBe(["transactionId", "durationMilliseconds"]);

        // The rolled back transaction: one rollback, and its refused commit.
        var rolledBackEvents = recorder.Events.Where(e => Equals(Payload(e, "transactionId"), rolledBack.Id.ToString())).ToArray();
        rolledBackEvents.Select(e => e.EventName).ShouldBe(["TransactionBegun", "TransactionRolledBack", "TransactionCommitFailed"]);
        rolledBackEvents[1].EventId.ShouldBe(31);
        rolledBackEvents[1].PayloadNames.ShouldBe(["transactionId", "cause"]);
        rolledBackEvents[1].Payload.ShouldBe([rolledBack.Id.ToString(), "Rollback"]);
        rolledBackEvents[2].EventId.ShouldBe(33);
        rolledBackEvents[2].Level.ShouldBe(EventLevel.Error);
        rolledBackEvents[2].PayloadNames.ShouldBe(["transactionId", "exceptionType"]);
        rolledBackEvents[2].Payload.ShouldBe([rolledBack.Id.ToString(), refusal.GetType().FullName]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a statement whose core threw, and leave the exception as it was")]
    public async Task ExecuteAsync_CoreThrows_ShouldReportTheFailureAndRethrowTheSameException()
    {
        // Arrange
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        await using var engine = new TestEngine(engineName);
        var database = new TestDatabase("thrown", engine);
        await using var session = new TestSession(database);
        var failure = new DatabaseException("the statement failed");
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act: a failed statement, then a cancelled one.
        session.ExecuteFailure = failure;
        var thrown = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new TestRequest()));
        var cancellation = new OperationCanceledException();
        session.ExecuteFailure = cancellation;
        // Recorded rather than asserted with Should.ThrowAsync, which reports a canceled task with an
        // exception of its own instead of the one the task holds.
        var cancelled = await Record.ExceptionAsync(async () => await session.ExecuteAsync("SELECT 1"));

        // Assert: the caller gets the core's own exceptions.
        thrown.ShouldBeSameAs(failure);
        cancelled.ShouldBeSameAs(cancellation);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(Payload(e, "sessionNumber"), session.SessionNumber) && e.EventName!.Contains("Statement", StringComparison.Ordinal)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["StatementStart", "StatementFailed", "StatementStop", "StatementStart", "StatementStop"]);
        events[0].Payload.ShouldBe(["thrown", session.SessionNumber, nameof(TestRequest)]);
        events[1].PayloadNames.ShouldBe(["database", "sessionNumber", "requestKind", "code", "exceptionType", "durationMilliseconds"]);
        Payload(events[1], "requestKind").ShouldBe(nameof(TestRequest));
        Payload(events[1], "exceptionType").ShouldBe(typeof(DatabaseException).FullName);
        events[1].Payload!.ShouldNotContain("the statement failed");
        Payload(events[2], "status").ShouldBe(nameof(QueryResultStatus.Error));
        Payload(events[2], "affectedCount").ShouldBe(-1L);
        Payload(events[3], "requestKind").ShouldBe(DatabaseEventSource.TextRequestKind);
        Payload(events[4], "status").ShouldBe(nameof(QueryResultStatus.Cancelled));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should write a failed result's first error diagnostic code, or an empty code when it has none")]
    public async Task StatementFailed_ErrorResult_ShouldWriteItsFirstErrorDiagnostic()
    {
        // Arrange: a result whose first diagnostic is a warning, and one with no diagnostics.
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        await using var session = new TestSession(new TestDatabase("diagnosed", engine));
        using var recorder = new DatabaseEventRecorder(EventLevel.Error);

        // Act
        session.Result = new DiagnosedResult(
            new Diagnostic("TESTW001", "a warning first", 0, 0, DiagnosticSeverity.Warning),
            new Diagnostic("TESTE001", "the error", 0, 0, DiagnosticSeverity.Error));
        await session.ExecuteAsync(new TestRequest());
        session.Result = new DiagnosedResult();
        await session.ExecuteAsync(new TestRequest());

        // Assert
        var failures = recorder.Events.Where(e => e.EventId == 28 && Equals(Payload(e, "sessionNumber"), session.SessionNumber)).ToArray();
        failures.Length.ShouldBe(2);
        Payload(failures[0], "code").ShouldBe("TESTE001");
        failures[0].Payload!.ShouldNotContain("the error");
        Payload(failures[1], "code").ShouldBe(string.Empty);
        Payload(failures[1], "exceptionType").ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should write no statement text for a parse error that quotes a string literal")]
    public async Task ExecuteAsync_ParseErrorQuotesALiteral_ShouldWriteNoStatementText()
    {
        // Arrange: the Sql parser quotes the token it stopped at, a string literal included.
        const string secret = "hunter2-secret-ssn-123-45-6789";
        await using var engine = SqlDatabaseEngine.Create("event-source-sql-" + Guid.NewGuid().ToString("N"), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("hygiene");
        await using var session = await database.CreateSessionAsync();
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act
        var failure = await Record.ExceptionAsync(async () => await session.ExecuteAsync($"CREATE TABLE leak (id INT NOT NULL) '{secret}'"));

        // Assert: the caller's exception quotes the literal; no event does (owner question Q3).
        failure.ShouldBeOfType<DatabaseParseException>().Message.ShouldContain(secret);
        var failed = recorder.Events.Where(e => e.EventId == 28 && Equals(Payload(e, "sessionNumber"), session.SessionNumber)).ShouldHaveSingleItem();
        Payload(failed, "exceptionType").ShouldBe(typeof(DatabaseParseException).FullName);
        recorder.Events.SelectMany(e => e.Payload ?? []).OfType<string>().ShouldNotContain(text => text.Contains(secret, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: A core that throws synchronously should throw from the call whether or not anyone listens")]
    public async Task ExecuteAsync_CoreThrowsSynchronously_ShouldThrowFromTheCallWhetherOrNotTraced()
    {
        // Arrange: the test session's and the test engine's cores throw from the call.
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        await engine.CreateDatabaseAsync("existing");
        await using var session = new TestSession(new TestDatabase("synchronous", engine));
        var failure = new DatabaseException("the statement failed");
        session.ExecuteFailure = failure;

        // Act and assert: the same delivery untraced and traced (event-source.md, rule 12).
        DatabaseEventSource.Log.IsEnabled().ShouldBeFalse("A listener left the Database event source enabled.");
        AssertThrowsFromTheCall();
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);
        AssertThrowsFromTheCall();
        recorder.Events.Count(e => e.EventId == 28 && Equals(Payload(e, "sessionNumber"), session.SessionNumber)).ShouldBe(2);
        recorder.Events.Count(e => e.EventId == 13 && Equals(Payload(e, "engineName"), engine.Name)).ShouldBe(2);

        void AssertThrowsFromTheCall()
        {
            Should.Throw<DatabaseException>(() => { _ = session.ExecuteAsync(new TestRequest()); }).ShouldBeSameAs(failure);
            Should.Throw<DatabaseException>(() => { _ = session.ExecuteAsync("SELECT 1"); }).ShouldBeSameAs(failure);
            Should.Throw<DatabaseException>(() => { _ = engine.CreateDatabaseAsync("existing"); });
            Should.Throw<DatabaseNotFoundException>(() => { _ = engine.DropDatabaseAsync("missing"); });
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a pending or an already faulted statement once and hand on its outcome")]
    public async Task ExecuteAsync_PendingAndFaultedCores_ShouldReportOnceAndHandOnTheOutcome()
    {
        // Arrange
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        await using var session = new TestSession(new TestDatabase("pending", engine));
        var failure = new DatabaseException("the statement failed");
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act: a core still running when the call returns, which then succeeds; another that then
        // fails; one that returns an already faulted task, as an async core that fails before its
        // first await does; and a statement after them, which is traced again.
        var succeeding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ExecuteBarrier = succeeding.Task;
        var pending = session.ExecuteAsync(new TestRequest());
        bool succeedingWasPending = !pending.IsCompleted;
        succeeding.SetResult();
        var result = await pending;

        var failing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ExecuteBarrier = failing.Task;
        session.ExecuteFailure = failure;
        var pendingFailure = session.ExecuteAsync(new TestRequest());
        bool failingWasPending = !pendingFailure.IsCompleted;
        failing.SetResult();
        var thrownLater = await Record.ExceptionAsync(async () => await pendingFailure);

        session.ExecuteBarrier = null;
        session.ExecuteFailureAsynchronously = true;
        var faulted = session.ExecuteAsync(new TestRequest());
        bool faultedAtReturn = faulted.IsFaulted;
        var thrownFaulted = await Record.ExceptionAsync(async () => await faulted);

        session.ExecuteFailure = null;
        await session.ExecuteAsync(new TestRequest());

        // Assert
        succeedingWasPending.ShouldBeTrue();
        result.ShouldBeSameAs(session.Result);
        failingWasPending.ShouldBeTrue();
        thrownLater.ShouldBeSameAs(failure);
        faultedAtReturn.ShouldBeTrue();
        thrownFaulted.ShouldBeSameAs(failure);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        recorder.Events
            .Where(e => Equals(Payload(e, "sessionNumber"), session.SessionNumber) && e.EventName!.StartsWith("Statement", StringComparison.Ordinal))
            .Select(e => e.EventName)
            .ShouldBe(
            [
                "StatementStart", "StatementStop",
                "StatementStart", "StatementFailed", "StatementStop",
                "StatementStart", "StatementFailed", "StatementStop",
                "StatementStart", "StatementStop",
            ]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should write a failed create, drop or statement once the core released the lock it threw under")]
    public async Task CreateDropAndExecute_CoreThrowsUnderItsLock_ShouldWriteOnceTheLockIsReleased()
    {
        // Arrange: the test engine's cores and the test session's core throw inside their own lock.
        // An event is written on the thread that threw, so the listener reads the lock there.
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        await using var engine = new TestEngine(engineName);
        await engine.CreateDatabaseAsync("existing");
        await using var session = new TestSession(new TestDatabase("locked", engine)) { ExecuteFailure = new DatabaseException("the statement failed") };
        long sessionNumber = session.SessionNumber;
        var heldDuringWrite = new ConcurrentQueue<(int EventId, bool Held)>();
        using var recorder = new DatabaseEventRecorder(EventLevel.Error)
        {
            OnWritten = e =>
            {
                if (e.EventId == 13 && Equals(Payload(e, "engineName"), engineName))
                {
                    heldDuringWrite.Enqueue((13, engine.HoldsRegistryLock));
                }
                else if (e.EventId == 28 && Equals(Payload(e, "sessionNumber"), sessionNumber))
                {
                    heldDuringWrite.Enqueue((28, Monitor.IsEntered(session.ExecuteGate)));
                }
            },
        };

        // Act
        Should.Throw<DatabaseException>(() => { _ = engine.CreateDatabaseAsync("existing"); });
        Should.Throw<DatabaseNotFoundException>(() => { _ = engine.DropDatabaseAsync("missing"); });
        Should.Throw<DatabaseException>(() => { _ = session.ExecuteAsync(new TestRequest()); });

        // Assert
        heldDuringWrite.ToArray().ShouldBe([(13, false), (13, false), (28, false)]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should write a refused commit once the transaction released its lock and its end gate")]
    public async Task CommitAsync_RefusedUnderTheTransactionLock_ShouldWriteOnceTheLockIsReleased()
    {
        // Arrange: a commit of a rolled back transaction is refused inside the base's lock, under its
        // end gate. Both are private to the base, so the test reads them by reflection.
        var transaction = new TestTransaction();
        await transaction.RollbackAsync();
        object sync = typeof(DatabaseTransaction).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transaction)!;
        var endGate = (SemaphoreSlim)typeof(DatabaseTransaction).GetField("_endGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transaction)!;
        string transactionId = transaction.Id.ToString();
        (bool LockHeld, int EndGateCount)? duringWrite = null;
        using var recorder = new DatabaseEventRecorder(EventLevel.Error)
        {
            OnWritten = e =>
            {
                if (e.EventId == 33 && Equals(Payload(e, "transactionId"), transactionId))
                {
                    duringWrite = (Monitor.IsEntered(sync), endGate.CurrentCount);
                }
            },
        };

        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        duringWrite.ShouldBe((false, 1));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should restore the default slow-statement threshold once the session that set it disables the source")]
    public async Task OnEventCommand_ThresholdSessionDisabled_ShouldRestoreTheDefault()
    {
        // Arrange: a Warning listener stands in for an in-process forwarder, and a tool session
        // briefly sets a 0 ms threshold.
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        await using var session = new TestSession(new TestDatabase("threshold", engine));
        using var forwarder = new DatabaseEventRecorder(EventLevel.Warning);
        using (new DatabaseEventRecorder(EventLevel.Warning, slowStatementThreshold: "0"))
        {
            DatabaseEventSource.Log.SlowStatementThresholdMilliseconds.ShouldBe(0);
        }

        // Act
        await session.ExecuteAsync(new TestRequest());

        // Assert
        DatabaseEventSource.Log.IsEnabled().ShouldBeTrue();
        DatabaseEventSource.Log.SlowStatementThresholdMilliseconds.ShouldBe(DatabaseEventSource.DefaultSlowStatementThresholdMilliseconds);
        forwarder.Events.Where(e => e.EventId == 27 && Equals(Payload(e, "sessionNumber"), session.SessionNumber)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a database that concurrent first opens share once")]
    public async Task OpenDatabaseAsync_ConcurrentFirstOpens_ShouldReportEachInstanceOnce()
    {
        // Arrange: a closed database, and an open core that holds every open until released.
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        await using var engine = new TestEngine(engineName);
        await (await engine.CreateDatabaseAsync("raced")).DisposeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OpenBarrier = release.Task;
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational);

        // Act: two opens both find no instance before the call and wait in the core together;
        // released, they share the one instance the first makes. Then an open of the open database,
        // and, once it is closed, an open that makes another instance.
        var first = engine.OpenDatabaseAsync("raced");
        var second = engine.OpenDatabaseAsync("raced");
        release.SetResult();
        var opened = await first;
        var shared = await second;
        engine.OpenBarrier = null;
        var again = await engine.OpenDatabaseAsync("raced");
        await opened.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("raced");

        // Assert: one open per instance.
        shared.ShouldBeSameAs(opened);
        again.ShouldBeSameAs(opened);
        reopened.ShouldNotBeSameAs(opened);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        recorder.Events.Where(e => Equals(Payload(e, "engineName"), engineName)).Select(e => e.EventName)
            .ShouldBe(["DatabaseOpened", "DatabaseClosed", "DatabaseOpened"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should write one statement pair for a Graph or Documents text statement that re-enters the typed path")]
    public async Task TextStatement_GraphAndDocuments_ShouldWriteOneStatementPairEach()
    {
        // Arrange: Graph and Documents parse a text statement and run the typed request through the
        // root's typed overload again on the same session (the plan's §4.1, "Re-entry").
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);
        await using var graphEngine = GraphDatabaseEngine.Create("event-source-graph-" + Guid.NewGuid().ToString("N"), new GraphDatabaseEngineOptions());
        var graph = await graphEngine.CreateDatabaseAsync("graph");
        await using var graphSession = await graph.CreateSessionAsync();
        await using var documentEngine = DocumentDatabaseEngine.Create("event-source-documents-" + Guid.NewGuid().ToString("N"), new DocumentDatabaseEngineOptions());
        var documents = await documentEngine.CreateDatabaseAsync("documents");
        await using (var setup = await documents.CreateSessionAsync())
        {
            await setup.CreateCollectionAsync("items");
        }

        await using var documentSession = await documents.CreateSessionAsync();

        // Act: one statement each, and a Graph statement that fails to parse.
        await graphSession.ExecuteAsync("INSERT (:Node {name: 'a'})");
        await documentSession.ExecuteAsync("SELECT id FROM items");
        var parseFailure = await Record.ExceptionAsync(async () => await graphSession.ExecuteAsync("THIS IS NOT GQL"));

        // Assert
        parseFailure.ShouldBeAssignableTo<DatabaseParseException>();
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        StatementEvents(recorder, graphSession.SessionNumber).ShouldBe(
            ["StatementStart", "StatementStop", "StatementStart", "StatementFailed", "StatementStop"]);
        StatementEvents(recorder, documentSession.SessionNumber).ShouldBe(["StatementStart", "StatementStop"]);
        recorder.Events
            .Where(e => e.EventId == 25 && (Equals(Payload(e, "sessionNumber"), graphSession.SessionNumber) || Equals(Payload(e, "sessionNumber"), documentSession.SessionNumber)))
            .ShouldAllBe(e => Equals(Payload(e, "requestKind"), DatabaseEventSource.TextRequestKind));

        static string?[] StatementEvents(DatabaseEventRecorder recorder, long sessionNumber)
            => [.. recorder.Events
                .Where(e => Equals(Payload(e, "sessionNumber"), sessionNumber) && e.EventName!.StartsWith("Statement", StringComparison.Ordinal))
                .Select(e => e.EventName)];
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report an aborted transaction once without its cause's message, and its session's teardown rollback")]
    public async Task AbortAsync_RepeatedFailureThenSessionClose_ShouldReportOnce()
    {
        // Arrange: a cause whose message quotes the statement, as a parse error's does.
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        var database = new TestDatabase("transactions", engine);
        var aborted = new TestTransaction();
        var session = new TestSession(database);
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act: an operation's failure aborts the transaction (twice: the second changes nothing),
        // its caller rolls it back and tries a commit; then a session closes with its transaction.
        await aborted.Abort(new DatabaseException("the operation failed near 'hunter2-secret'"));
        await aborted.Abort(new DatabaseException("a later failure"));
        await aborted.RollbackAsync();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await aborted.CommitAsync());
        var open = await session.BeginTransactionAsync();
        await session.DisposeAsync();

        // Assert: the model's refusal repeats the cause; neither event does (owner question Q3).
        refusal.Message.ShouldContain("hunter2-secret");
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var abortedEvents = recorder.Events.Where(e => Equals(Payload(e, "transactionId"), aborted.Id.ToString())).ToArray();
        abortedEvents.Select(e => e.EventName).ShouldBe(["TransactionAborted", "TransactionCommitFailed"]);
        abortedEvents[0].EventId.ShouldBe(32);
        abortedEvents[0].PayloadNames.ShouldBe(["transactionId", "exceptionType"]);
        abortedEvents[0].Payload.ShouldBe([aborted.Id.ToString(), typeof(DatabaseException).FullName]);
        abortedEvents[1].EventId.ShouldBe(33);
        abortedEvents[1].PayloadNames.ShouldBe(["transactionId", "exceptionType"]);
        abortedEvents[1].Payload.ShouldBe([aborted.Id.ToString(), refusal.GetType().FullName]);

        var openEvents = recorder.Events.Where(e => Equals(Payload(e, "transactionId"), open.Id.ToString())).ToArray();
        openEvents.Select(e => e.EventName).ShouldBe(["TransactionBegun", "TransactionRolledBack"]);
        openEvents[1].Payload.ShouldBe([open.Id.ToString(), "SessionClosed"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a worker's passes, its unfinished work and a give-up its engine could not complete")]
    public async Task RunIteration_UnfinishedThenFailedPass_ShouldReportEachOnce()
    {
        // Arrange: a free worker whose first pass leaves database a unfinished and whose second fails
        // on it; and an engine whose leaf refuses a give-up.
        string name = "event-source-" + Guid.NewGuid().ToString("N");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a").ShouldBeTrue();
            if (pass == 1)
            {
                self.Unfinished("a");
            }
            else
            {
                self.Fail("a", new InvalidOperationException("the pass failed on a"), TimeSpan.Zero);
            }
        }, name: name);

        var clock = new ManualTimeProvider();
        string engineName = name + "-engine";
        await using var engine = new TestEngine(engineName, workerFailureWindow: TimeSpan.FromSeconds(1), workerFailureMinimumPasses: 1, time: clock)
        {
            TakeOfflineFailure = new InvalidOperationException("the leaf refused"),
        };
        await engine.CreateDatabaseAsync("b");
        var givingUp = new ScriptedWorker((self, pass) =>
        {
            self.Begin("b");
            self.Fail("b", new InvalidOperationException("checkpoint failed"), TimeSpan.Zero);
            clock.Advance(TimeSpan.FromSeconds(2));
        }, name: engineName + "/checkpoint");
        engine.Attach(givingUp);
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);
        givingUp.RunIteration(CancellationToken.None);
        givingUp.RunIteration(CancellationToken.None);
        await recorder.WaitForAsync(e => e.EventId == 17 && Equals(e.Payload?[0], engineName + "/checkpoint"));

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], name)).ToArray();
        events.Select(e => e.EventName).ShouldBe(
            ["WorkerPassStart", "WorkerDatabaseUnfinished", "WorkerPassStop", "WorkerPassStart", "WorkerFailed", "WorkerPassStop"]);
        events[0].EventId.ShouldBe(14);
        events[0].Keywords.HasFlag(DatabaseEventSource.Keywords.Workers).ShouldBeTrue();
        events[0].PayloadNames.ShouldBe(["workerName", "workerKind", "pass"]);
        events[0].Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), 1L]);
        events[1].EventId.ShouldBe(16);
        events[1].PayloadNames.ShouldBe(["workerName", "workerKind", "database"]);
        events[1].Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), "a"]);
        events[2].EventId.ShouldBe(15);
        events[2].PayloadNames.ShouldBe(["workerName", "workerKind", "pass", "status", "durationMilliseconds"]);
        Payload(events[2], "status").ShouldBe(nameof(QueryResultStatus.Success));
        Payload(events[5], "pass").ShouldBe(2L);
        Payload(events[5], "status").ShouldBe(nameof(QueryResultStatus.Error));

        var giveUpFailed = recorder.Events.Where(e => e.EventId == 17 && Equals(e.Payload?[0], engineName + "/checkpoint")).ShouldHaveSingleItem();
        giveUpFailed.EventName.ShouldBe("WorkerGiveUpFailed");
        giveUpFailed.Level.ShouldBe(EventLevel.Error);
        giveUpFailed.PayloadNames.ShouldBe(["workerName", "workerKind", "database", "exceptionType", "exceptionMessage"]);
        giveUpFailed.Payload.ShouldBe([engineName + "/checkpoint", nameof(DatabaseEngineWorkerKind.Checkpoint), "b", typeof(InvalidOperationException).FullName, "the leaf refused"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a worker loop the engine's pump saw end early")]
    public async Task RecordWorkerRunFault_LoopEscaped_ShouldWriteWorkerLoopFaulted()
    {
        // Arrange: the base's loop never returns early or throws but for an out-of-memory failure, so
        // the test reaches the pump's recorder directly, as the pump's two exits do.
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        await using var engine = new TestEngine(engineName);
        var worker = new ScriptedWorker((_, _) => { }, name: engineName + "/pumped");
        using var recorder = new DatabaseEventRecorder(EventLevel.Error);

        // Act
        engine.RecordWorkerRunFault(worker, new InvalidOperationException("the loop escaped"));

        // Assert
        engine.State.ShouldBe(EngineState.Faulted);
        var faulted = recorder.Events.Where(e => Equals(e.Payload?[0], engineName)).ShouldHaveSingleItem();
        faulted.EventId.ShouldBe(8);
        faulted.EventName.ShouldBe("WorkerLoopFaulted");
        faulted.Level.ShouldBe(EventLevel.Error);
        faulted.PayloadNames.ShouldBe(["engineName", "workerName", "workerKind", "exceptionType", "exceptionMessage"]);
        faulted.Payload.ShouldBe([engineName, engineName + "/pumped", nameof(DatabaseEngineWorkerKind.Checkpoint), typeof(InvalidOperationException).FullName, "the loop escaped"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a server's start, failed start and stop once each, and no failure for a canceled start")]
    public async Task StartAsync_StartStopAndFailedStart_ShouldReportOnceEach()
    {
        // Arrange
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        await using var engine = new TestEngine(engineName);
        var server = new TestServer(engine);
        var failing = new TestServer(engine) { StartFailure = new InvalidOperationException("bind failed") };
        var canceled = new TestServer(engine) { StartFailure = new OperationCanceledException() };
        var idle = new TestServer(engine);
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational);

        // Act: start twice and stop twice; a failed start; a canceled start, which is not a failure;
        // and a stop of a server that never started.
        await server.StartAsync();
        await server.StartAsync();
        await server.StopAsync();
        await server.StopAsync();
        await Should.ThrowAsync<InvalidOperationException>(async () => await failing.StartAsync());
        (await Record.ExceptionAsync(async () => await canceled.StartAsync())).ShouldBeOfType<OperationCanceledException>();
        await idle.StopAsync();

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(Payload(e, "engineName"), engineName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["ServerStarted", "ServerStopped", "ServerStartFailed"]);
        events[0].EventId.ShouldBe(18);
        events[0].PayloadNames.ShouldBe(["engineName", "model", "serverType"]);
        events[0].Payload.ShouldBe([engineName, nameof(EngineModel.Sql), nameof(TestServer)]);
        events[1].EventId.ShouldBe(20);
        events[1].PayloadNames.ShouldBe(["engineName", "model", "serverType", "durationMilliseconds"]);
        events[2].EventId.ShouldBe(19);
        events[2].Level.ShouldBe(EventLevel.Error);
        events[2].PayloadNames.ShouldBe(["engineName", "model", "serverType", "exceptionType", "exceptionMessage"]);
        events[2].Payload.ShouldBe([engineName, nameof(EngineModel.Sql), nameof(TestServer), typeof(InvalidOperationException).FullName, "bind failed"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should end a pass the engine's stop cancelled with a Cancelled stop, and no failure")]
    public void RunIteration_PassCancelled_ShouldWriteACancelledStop()
    {
        // Arrange: a pass that observes its token after the engine's stop cancelled it.
        string name = "event-source-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var worker = new ScriptedWorker((self, pass) =>
        {
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
        }, name: name);
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act
        Should.Throw<OperationCanceledException>(() => worker.RunIteration(stop.Token));

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], name)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["WorkerPassStart", "WorkerPassStop"]);
        Payload(events[1], "status").ShouldBe(nameof(QueryResultStatus.Cancelled));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should end a pass that saw the engine's stop and returned with a Cancelled stop, and no failure")]
    public void RunIteration_PassReturnsAfterStop_ShouldWriteACancelledStop()
    {
        // Arrange: a pass that sees the engine's stop and returns early without throwing, as the
        // flush and maintenance workers do between databases.
        string name = "event-source-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var worker = new ScriptedWorker((self, pass) => stop.Cancel(), name: name);
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act
        bool succeeded = worker.RunIteration(stop.Token);

        // Assert: the pass did not fail, but it stopped early, so it did not succeed either.
        succeeded.ShouldBeTrue();
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], name)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["WorkerPassStart", "WorkerPassStop"]);
        Payload(events[1], "status").ShouldBe(nameof(QueryResultStatus.Cancelled));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should write no StatementStop for a statement whose start was not written")]
    public async Task StatementEnd_StartNotWritten_ShouldWriteTheFailureButNoStop()
    {
        // Arrange: a statement whose start was not written, as for a listener that attached while
        // it ran (System.Net.Http's RequestStop shape), and one whose start was.
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        await using var session = new TestSession(new TestDatabase("unstarted", engine));
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act
        DatabaseEventSource.Log.StatementThrew(session, null, new InvalidOperationException("boom"), startWritten: false, Stopwatch.GetTimestamp());
        DatabaseEventSource.Log.StatementCompleted(session, null, result: null, startWritten: false, Stopwatch.GetTimestamp());
        DatabaseEventSource.Log.StatementThrew(session, null, new InvalidOperationException("boom"), startWritten: true, Stopwatch.GetTimestamp());

        // Assert: the failure is written either way; a stop only for the statement that started.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(Payload(e, "sessionNumber"), session.SessionNumber)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["StatementFailed", "StatementFailed", "StatementStop"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should bound a principal the peer sent at 256 characters")]
    public void Authenticate_LongPrincipal_ShouldWriteItBounded()
    {
        // Arrange: a principal no authenticator bounded, longer than any event writes.
        var session = new TestServerSession();
        string principal = new('p', 300);
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational);

        // Act
        session.Authenticate(principal);

        // Assert
        var authenticated = recorder.Events.Where(e => e.EventId == 22 && Equals(e.Payload?[0], session.Id)).ShouldHaveSingleItem();
        authenticated.Payload![1].ShouldBe(new string('p', DatabaseEventSource.MaxNameLength) + "...");
        ((string)authenticated.Payload![1]!).Length.ShouldBe(259);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report a server session's negotiated version and authenticated principal")]
    public void SetNegotiatedVersion_Handshake_ShouldReportVersionAndPrincipal()
    {
        // Arrange
        var session = new TestServerSession();
        using var recorder = new DatabaseEventRecorder(EventLevel.Verbose);

        // Act
        session.Negotiate(new ProtocolVersion(1, 2));
        session.Authenticate("alice");

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], session.Id)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["ServerSessionNegotiated", "ServerSessionAuthenticated"]);
        events[0].EventId.ShouldBe(21);
        events[0].Keywords.HasFlag(DatabaseEventSource.Keywords.Sessions).ShouldBeTrue();
        events[0].PayloadNames.ShouldBe(["sessionId", "protocolVersion"]);
        events[0].Payload.ShouldBe([session.Id, "1.2"]);
        events[1].EventId.ShouldBe(22);
        events[1].Level.ShouldBe(EventLevel.Informational);
        events[1].PayloadNames.ShouldBe(["sessionId", "principal"]);
        events[1].Payload.ShouldBe([session.Id, "alice"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report an engine disposal whose components failed to close")]
    public async Task DisposeAsync_ComponentFails_ShouldReportFailureBeforeStop()
    {
        // Arrange
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        var engine = new TestEngine(engineName) { DisposeFailure = new InvalidOperationException("the databases failed to close") };
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational);

        // Act
        await Should.ThrowAsync<AggregateException>(async () => await engine.DisposeAsync());
        await engine.DisposeAsync();

        // Assert: one disposal, written once, though disposed twice.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(Payload(e, "engineName"), engineName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["EngineDisposeStart", "EngineDisposeFailed", "EngineDisposeStop"]);
        events[1].EventId.ShouldBe(34);
        events[1].Level.ShouldBe(EventLevel.Error);
        events[1].PayloadNames.ShouldBe(["engineName", "model", "failureCount", "exceptionType", "exceptionMessage"]);
        events[1].Payload.ShouldBe([engineName, nameof(EngineModel.Sql), 1, typeof(InvalidOperationException).FullName, "the databases failed to close"]);
        Payload(events[2], "failureCount").ShouldBe(1);
        Payload(events[2], "status").ShouldBe(nameof(QueryResultStatus.Error));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should set the slow-statement threshold from each enabling session")]
    public void SlowStatementThreshold_EnablingSessions_ShouldSetItOrRestoreTheDefault()
    {
        // Act and assert: an argument sets it; none, or one that does not parse, restores the default.
        using (new DatabaseEventRecorder(EventLevel.Warning, slowStatementThreshold: "250"))
        {
            DatabaseEventSource.Log.SlowStatementThresholdMilliseconds.ShouldBe(250);
        }

        using (new DatabaseEventRecorder(EventLevel.Warning))
        {
            DatabaseEventSource.Log.SlowStatementThresholdMilliseconds.ShouldBe(DatabaseEventSource.DefaultSlowStatementThresholdMilliseconds);
        }

        using (new DatabaseEventRecorder(EventLevel.Warning, slowStatementThreshold: "-5"))
        {
            DatabaseEventSource.Log.SlowStatementThresholdMilliseconds.ShouldBe(DatabaseEventSource.DefaultSlowStatementThresholdMilliseconds);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should not report a quick statement as slow under the default threshold")]
    public async Task SlowStatement_DefaultThreshold_ShouldNotBeWrittenForAQuickStatement()
    {
        // Arrange
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        await using var session = new TestSession(new TestDatabase("quick", engine));
        using var recorder = new DatabaseEventRecorder(EventLevel.Warning);

        // Act
        await session.ExecuteAsync(new TestRequest());

        // Assert
        recorder.Events.Where(e => Equals(Payload(e, "sessionNumber"), session.SessionNumber)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should publish current-sessions, which returns to its starting value")]
    public async Task DatabaseSession_OpenAndDispose_ShouldRestoreCurrentSessions()
    {
        // Arrange
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        var database = new TestDatabase("counted", engine);
        long before = DatabaseEventSource.Log.CurrentSessions;
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational, counterIntervalSeconds: 0.1);

        // Act
        var first = new TestSession(database);
        var second = new TestSession(database);
        long open = DatabaseEventSource.Log.CurrentSessions;
        await recorder.WaitForCounterAsync("current-sessions");
        await first.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();

        // Assert
        open.ShouldBe(before + 2);
        DatabaseEventSource.Log.CurrentSessions.ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: The statement path should allocate nothing more than its core while nobody listens")]
    public async Task ExecuteAsync_NoListener_ShouldAllocateNothingMoreThanTheCore()
    {
        // Arrange: the core completes synchronously and allocates nothing, so the public member's
        // own cost is the whole difference.
        DatabaseEventSource.Log.IsEnabled().ShouldBeFalse("A listener left the Database event source enabled.");
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        await using var session = new TestSession(new TestDatabase("hot", engine));
        var request = new TestRequest();
        const int iterations = 10_000;
        Measure(() => session.ExecuteAsync(request), 100);
        Measure(() => session.ExecuteCoreDirectly(request), 100);
        Measure(() => session.ExecuteAsync("SELECT 1"), 100);

        // Act
        long core = Measure(() => session.ExecuteCoreDirectly(request), iterations);
        long typed = Measure(() => session.ExecuteAsync(request), iterations);
        long text = Measure(() => session.ExecuteAsync("SELECT 1"), iterations);

        // Assert
        core.ShouldBe(0);
        typed.ShouldBe(core);
        text.ShouldBe(core);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: The statement path should allocate nothing for a synchronous core under a Warning listener")]
    public async Task ExecuteAsync_WarningListener_ShouldAllocateNothingForASynchronousCore()
    {
        // Arrange: a Warning listener (an application forwarding at Information) takes the timed
        // path: two timestamps, and no event for a quick statement that succeeds. A core that
        // completed is handed on without an async wrapper, so no state machine is allocated in an
        // optimized or an unoptimized build.
        await using var engine = new TestEngine("event-source-" + Guid.NewGuid().ToString("N"));
        await using var session = new TestSession(new TestDatabase("warm", engine));
        var request = new TestRequest();
        const int iterations = 10_000;
        using var recorder = new DatabaseEventRecorder(EventLevel.Warning);
        Measure(() => session.ExecuteAsync(request), 100);
        Measure(() => session.ExecuteAsync("SELECT 1"), 100);

        // Act
        long typed = Measure(() => session.ExecuteAsync(request), iterations);
        long text = Measure(() => session.ExecuteAsync("SELECT 1"), iterations);

        // Assert
        typed.ShouldBe(0);
        text.ShouldBe(0);
        recorder.Events.Where(e => Equals(Payload(e, "sessionNumber"), session.SessionNumber)).ShouldBeEmpty();
    }

    // Runs the call the given number of times on this thread and returns the bytes it allocated.
    private static long Measure(Func<ValueTask<QueryResult>> execute, int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            var pending = execute();
            if (!pending.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("The measured call did not complete synchronously.");
            }

            _ = pending.Result;
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static object? Payload(EventWrittenEventArgs eventData, string name)
    {
        int index = eventData.PayloadNames?.IndexOf(name) ?? -1;
        return index < 0 ? null : eventData.Payload![index];
    }

    /// <summary>
    /// A result a session refused with diagnostics: <see cref="QueryResultStatus.Error"/>.
    /// </summary>
    private sealed class DiagnosedResult(params Diagnostic[] diagnostics) : QueryResult
    {
        public override QueryResultStatus Status => QueryResultStatus.Error;

        public override long AffectedCount => 0;

        public override IReadOnlyList<Diagnostic>? Diagnostics { get; } = diagnostics;
    }

    /// <summary>
    /// Records the events and counter names the Database event source writes, and disables the
    /// source again when it is disposed.
    /// </summary>
    private sealed class DatabaseEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();
        private readonly ConcurrentDictionary<string, bool> _counters = new(StringComparer.Ordinal);

        public DatabaseEventRecorder(
            EventLevel level,
            EventKeywords keywords = EventKeywords.All,
            string? slowStatementThreshold = null,
            double? counterIntervalSeconds = null)
        {
            Dictionary<string, string?>? arguments = null;
            if (slowStatementThreshold is not null)
            {
                (arguments ??= [])[DatabaseEventSource.SlowStatementThresholdArgument] = slowStatementThreshold;
            }

            if (counterIntervalSeconds is { } interval)
            {
                (arguments ??= [])["EventCounterIntervalSec"] = interval.ToString(CultureInfo.InvariantCulture);
            }

            EnableEvents(DatabaseEventSource.Log, level, keywords, arguments);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        /// <summary>
        /// Gets or sets a callback run for every event on the thread that writes it, before the
        /// event is recorded.
        /// </summary>
        public Action<EventWrittenEventArgs>? OnWritten { get; init; }

        public async Task WaitForAsync(Func<EventWrittenEventArgs, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(_wait);
            while (!_events.Any(predicate))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public async Task WaitForCounterAsync(string name)
        {
            using var timeout = new CancellationTokenSource(_wait);
            while (!_counters.ContainsKey(name))
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        public override void Dispose()
        {
            // Disabling sends the source the command that turns it off once no listener is left, so
            // a later test sees it disabled.
            DisableEvents(DatabaseEventSource.Log);
            base.Dispose();
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (!ReferenceEquals(eventData.EventSource, DatabaseEventSource.Log))
            {
                return;
            }

            if (string.Equals(eventData.EventName, "EventCounters", StringComparison.Ordinal))
            {
                if (eventData.Payload is [IDictionary<string, object?> counter, ..]
                    && counter.TryGetValue("Name", out object? name)
                    && name is string counterName)
                {
                    _counters[counterName] = true;
                }

                return;
            }

            OnWritten?.Invoke(eventData);
            _events.Enqueue(eventData);
        }
    }
}
