using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Serializes the tests that observe the SQL model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(SqlDatabaseEventSourceCollection), DisableParallelization = true)]
public class SqlDatabaseEventSourceCollection
{
}

/// <summary>
/// The SQL model's event source against the repository's EventSource convention, and the server
/// events it writes for real sessions over the in-memory driver: each event once, with its
/// declared payload, and the server-session gauge back where it started (database event-sources
/// plan, section 4.9). The Key-value, Graph and Blob suites check the same ids, names and payloads
/// against their own sources.
/// </summary>
[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed partial class SqlDatabaseEventSourceTests
{
    private static readonly string[] SessionClosedPayload = ["sessionId", "reason", "durationMilliseconds"];

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - StatementPlanned: Should report final metrics once for a real statement")]
    public async Task StatementPlanned_ExecutedStatement_ShouldReportItsFinalMetricsOnce()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("plans", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items VALUES (1), (2)", cancellationToken: CancellationToken.None);
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);

        // Act
        var result = await session.ExecuteAsync("SELECT id FROM items WHERE id = 2", cancellationToken: CancellationToken.None);

        // Assert
        result.Status.ShouldBe(QueryResultStatus.Success);
        var planned = NewEvent(recorder, 10, "StatementPlanned", "database", "accessPath", "recordsExamined");
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        planned.Payload.ShouldBe(["plans", metrics.AccessPath, metrics.RecordsExamined]);
        metrics.AccessPath.ShouldStartWith("seek:");
        metrics.RecordsExamined.ShouldBe(1);
        planned.Keywords.HasFlag(SqlDatabaseEventSource.Keywords.Statements).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - DdlSelfCommitted: Should report a real catalog publication once")]
    public async Task DdlSelfCommitted_CreateTable_ShouldReportOnce()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("ddl", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);

        // Act
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);

        // Assert
        NewEvent(recorder, 12, "DdlSelfCommitted", "database", "commandType").Payload.ShouldBe(["ddl", "Create"]);
        NewEvent(recorder, 10, "StatementPlanned", "database", "accessPath", "recordsExamined");
        session.LastStatementMetrics.ShouldNotBeNull().SelfCommits.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - StatementTooComplex: Should report text and typed nesting refusals once")]
    [InlineData("Text")]
    [InlineData("Typed")]
    [InlineData("Diagnostic")]
    public async Task StatementTooComplex_NestingRefusal_ShouldReportDepthOnce(string path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions { ExpressionNestingLimit = 32 });
        var database = await engine.CreateDatabaseAsync("depth", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string text = "SELECT " + new string('(', 64) + "1" + new string(')', 64) + " FROM items";
        var request = path switch
        {
            "Typed" => SqlQueryRequest.FromSql(text, null, new SqlQueryParserOptions { ExpressionNestingLimit = 128 }),
            "Diagnostic" => new SqlQueryRequest((SqlQueryStatement)new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 32 }).Parse(text)),
            _ => null,
        };
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Warning);

        // Act
        if (request is not null)
        {
            (await session.ExecuteAsync(request, CancellationToken.None)).Status.ShouldBe(QueryResultStatus.Error);
        }
        else
        {
            await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(text, cancellationToken: CancellationToken.None));
        }

        // Assert
        var refused = NewEvent(recorder, 11, "StatementTooComplex", "database", "depth");
        refused.Payload![0].ShouldBe("depth");
        // The parser stops before entering the forbidden level and records the deepest level
        // reached; the typed request retains its caller's complete measure.
        ((int)refused.Payload[1]!).ShouldBe(path == "Typed" ? 64 : 32);
        refused.Level.ShouldBe(EventLevel.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - ExtensionRegistered: Should report accepted built-ins and application registrations once")]
    public void ExtensionRegistered_RegistryAdd_ShouldReportEveryAcceptedEntry()
    {
        // Arrange: initialize the process-wide standard catalogs before observing a new registry.
        _ = SqlFunctionCatalog.Standard;
        _ = SqlTypeCatalog.Standard;
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);

        // Act
        var builder = SqlDatabaseEngine.CreateBuilder(UniqueEngineName());
        var scalar = SqlScalarFunction.Create("trace_scalar", static (long value) => value);
        var domain = SqlType.CreateDomain("trace_domain", SqlType.BigInt);
        var cast = SqlCast.Create<long, long>(SqlType.BigInt, domain, static value => value, SqlCastContext.Explicit);
        builder.Functions.Add(scalar);
        builder.Types.Add(domain).AddCast(cast);
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(scalar));
        Should.Throw<InvalidOperationException>(() => builder.Types.Add(domain));
        Should.Throw<InvalidOperationException>(() => builder.Types.AddCast(cast));

        // Assert: each overload is a registration, even if several have the same name.
        var expected = builder.Functions.Select(function => (Kind: function.Kind.ToString(), function.Name))
            .Concat(builder.Types.Select(type => (Kind: "Type", type.Name)))
            .Concat(builder.Types.Casts.Select(entry => (Kind: "Cast", Name: entry.Source.Name + " -> " + entry.Target.Name)))
            .OrderBy(entry => entry.Kind).ThenBy(entry => entry.Name).ToArray();
        var written = recorder.Events.Where(entry => entry.EventId == 16).ToArray();
        written.Select(entry => ((string)entry.Payload![0]!, (string)entry.Payload![1]!))
            .OrderBy(entry => entry.Item1).ThenBy(entry => entry.Item2).ShouldBe(expected);
        foreach (var entry in written)
        {
            entry.EventName.ShouldBe("ExtensionRegistered");
            entry.PayloadNames.ShouldBe(["kind", "name"]);
        }
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    private static EventWrittenEventArgs NewEvent(EventSourceRecorder recorder, int id, string name, params string[] payloadNames)
    {
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        var written = recorder.Events.Where(entry => entry.EventId == id).ShouldHaveSingleItem();
        written.EventName.ShouldBe(name);
        written.PayloadNames.ShouldBe(payloadNames);
        return written;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - StatementTooComplex: Should report an execution stack backstop without statement text")]
    public async Task StatementTooComplex_ExecutionStackFailure_ShouldReportUnknownDepthOnce()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder(UniqueEngineName());
        builder.Functions.Add(SqlScalarFunction.Create<long, long>("trace_stack", static value =>
            throw new InsufficientExecutionStackException("private statement content")));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("stack", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id BIGINT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: CancellationToken.None);
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT trace_stack(id) FROM items", cancellationToken: CancellationToken.None));

        // Assert
        failure.Message.ShouldStartWith("COHSQLE004");
        NewEvent(recorder, 11, "StatementTooComplex", "database", "depth").Payload.ShouldBe(["stack", -1]);
        NewEvent(recorder, 10, "StatementPlanned", "database", "accessPath", "recordsExamined");
        recorder.Events.SelectMany(entry => entry.Payload ?? []).ShouldNotContain("private statement content");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Statements keyword: Should exclude statement detail when only sessions are enabled")]
    public async Task ExecuteAsync_StatementsKeywordExcluded_ShouldWriteNoStatementDetail()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("keyword", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        recorder.EnableEvents(SqlDatabaseEventSource.Log, EventLevel.Verbose, SqlDatabaseEventSource.Keywords.Sessions);

        // Act
        await session.ExecuteAsync("CREATE TABLE items (id INT)", cancellationToken: CancellationToken.None);

        // Assert
        session.LastStatementMetrics.ShouldNotBeNull().SelfCommits.ShouldBeGreaterThan(0);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0 || entry.EventId == 10 || entry.EventId == 12);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Statement tracing: Should allocate nothing for disabled statement and registry hooks")]
    public void StatementTracing_DisabledHotPath_ShouldAllocateNothing()
    {
        // Arrange: the same metrics instance execution already owns; all payload conversion is
        // inside the source's guard, including the DDL enum and cast signature.
        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        var metrics = new SqlStatementMetrics { AccessPath = "seek:PK_items", RecordsExamined = 1 };
        var function = SqlScalarFunction.Create("trace_alloc", static (long value) => value);
        var type = SqlType.CreateDomain("trace_alloc_type", SqlType.BigInt);
        var cast = SqlCast.Create<long, long>(SqlType.BigInt, type, static value => value, SqlCastContext.Explicit);
        DatabaseName database = "allocations";
        Write();

        // Act
        long allocated = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Write();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert
        allocated.ShouldBe(0);

        void Write()
        {
            for (int index = 0; index < 10_000; index++)
            {
                SqlDatabaseEventSource.Log.StatementPlanned(database, metrics);
                SqlDatabaseEventSource.Log.StatementTooComplex(database, 32);
                SqlDatabaseEventSource.Log.DdlSelfCommitted(database, SqlQueryCommandType.Create);
                SqlDatabaseEventSource.Log.ExtensionRegistered(function);
                SqlDatabaseEventSource.Log.ExtensionRegistered(type);
                SqlDatabaseEventSource.Log.ExtensionRegistered(cast);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Statement allocation baseline: Every unsafe accessor should resolve")]
    public async Task InvokeBindingsAsync_CurrentSession_ShouldResolveEveryUnsafeAccessor()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("bindings", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        var baseline = new SqlStatementAllocationBaseline(session);

        // Act: exercise the field, statement executor and SelfCommitted method directly,
        // including the binding normally reached only through an exception filter.
        bool selfCommitted = await baseline.InvokeBindingsAsync("SELECT id FROM items", CancellationToken.None);

        // Assert
        selfCommitted.ShouldBeFalse("A SELECT cannot commit DDL.");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Statement tracing: A real statement should retain its allocation baseline while disabled")]
    [InlineData("SELECT id FROM items WHERE id = 1")]
    [InlineData("SELECT id, id FROM items WHERE id = 1")]
    public async Task ExecuteAsync_NoListener_ShouldRetainStatementAllocationBaseline(string statement)
    {
        // Arrange: parsing, binding, planning and materialization all run through the public
        // session path. One snapshot transaction holds the database state steady across rounds.
        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse("A listener left the SQL event source enabled.");
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("allocations", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        var baseline = new SqlStatementAllocationBaseline(session);
        const int iterations = 1_000;
        Measure(100, reference: true);
        Measure(100, reference: false);

        // Act: take the fewest bytes of three warmed rounds, as DatabaseEventSourceTests does.
        // Runtime bookkeeping can charge an occasional allocation to this thread; an allocation
        // in ExecuteRequestAsync, its finally, or an event argument repeats in every round.
        long allocated = long.MaxValue;
        long baselineAllocated = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            baselineAllocated = Math.Min(baselineAllocated, Measure(iterations, reference: true));
            allocated = Math.Min(allocated, Measure(iterations, reference: false));
        }

        // Assert
        // The reference omits tracing, but uses the same current parser and executor. The
        // two projections deliberately allocate different totals; neither pins a byte count.
        (allocated - baselineAllocated).ShouldBe(0);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldStartWith("seek:");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(1);
        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();

        long Measure(int count, bool reference)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < count; index++)
            {
                var pending = reference
                    ? baseline.ExecuteAsync(statement, CancellationToken.None)
                    : session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
                if (!pending.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException("The measured statement did not complete synchronously.");
                }

                var result = pending.Result;
                if (result is not QueryResultSet rows || result.Status != QueryResultStatus.Success)
                {
                    throw new InvalidOperationException("The measured statement did not produce its result set.");
                }

                var dispose = rows.DisposeAsync();
                if (!dispose.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException("The measured result did not dispose synchronously.");
                }
                dispose.GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should be named for its assembly")]
    public void GetName_SqlDatabaseEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(SqlDatabaseEventSource));

        // Assert
        name.ShouldBe(typeof(SqlDatabaseEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Sql");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(SqlDatabaseEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Sql", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report an accepted session and its close once each and restore the gauge")]
    public async Task SessionLifecycle_AcceptAndTerminate_ShouldReportEachOnceAndRestoreTheGauge()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        long currentBefore = SqlDatabaseEventSource.Log.CurrentServerSessions;
        long totalBefore = SqlDatabaseEventSource.Log.TotalServerSessions;
        long currentWhileOpen;

        // Act
        await using (var harness = await ServerTestHarness.StartAsync(engineName: engineName))
        {
            await using var client = await harness.DialAsync();
            await client.HandshakeAsync();
            currentWhileOpen = SqlDatabaseEventSource.Log.CurrentServerSessions;
            await client.SendAsync(ProtocolMessageType.Terminate);
            (await client.ReadAsync()).ShouldBeNull();
            await ServerTestHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);
        }

        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        currentWhileOpen.ShouldBe(currentBefore + 1);
        SqlDatabaseEventSource.Log.CurrentServerSessions.ShouldBe(currentBefore);
        SqlDatabaseEventSource.Log.TotalServerSessions.ShouldBe(totalBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        var accepted = recorder.Events.Where(e => e.EventId == 1 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        accepted.EventName.ShouldBe("SessionAccepted");
        accepted.Level.ShouldBe(EventLevel.Verbose);
        (accepted.Keywords & SqlDatabaseEventSource.Keywords.Sessions).ShouldBe(SqlDatabaseEventSource.Keywords.Sessions);
        accepted.PayloadNames.ShouldBe(["engineName", "sessionId", "activeSessions"]);
        accepted.Payload![2].ShouldBe(1);

        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem();
        closed.EventName.ShouldBe("SessionClosed");
        closed.Level.ShouldBe(EventLevel.Verbose);
        closed.PayloadNames.ShouldBe(SessionClosedPayload);
        closed.Payload![1].ShouldBe("Terminated");
        ((double)closed.Payload[2]!).ShouldBeGreaterThan(0);
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([5]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a connection beyond the session limit as rejected once")]
    public async Task SessionRejected_BeyondMaxSessions_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        long rejectedBefore = SqlDatabaseEventSource.Log.TotalRejectedSessions;
        await using var harness = await ServerTestHarness.StartAsync(options => options.MaxSessions = 1, engineName: engineName);
        await using var first = await harness.DialAsync();
        await first.HandshakeAsync();

        // Act
        await using var second = await harness.DialAsync();
        await second.ExpectAsync(ProtocolMessageType.Error);
        var rejected = await recorder.WaitForAsync(e => e.EventId == 2 && Equals(e.Payload![0], engineName), TestTimeout.Token(30));

        // Assert
        recorder.Events.Where(e => e.EventId == 2 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        rejected.EventName.ShouldBe("SessionRejected");
        rejected.Level.ShouldBe(EventLevel.Warning);
        rejected.PayloadNames.ShouldBe(["engineName", "reason", "activeSessions", "maxSessions"]);
        rejected.Payload.ShouldBe([engineName, "SessionLimit", 1, 1]);
        SqlDatabaseEventSource.Log.TotalRejectedSessions.ShouldBe(rejectedBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report each handshake refusal once with its code")]
    [InlineData("StartupExpected", nameof(ProtocolErrorCode.ProtocolViolation))]
    [InlineData("UnsupportedVersion", nameof(ProtocolErrorCode.UnsupportedVersion))]
    [InlineData("DatabaseNotFound", nameof(ProtocolErrorCode.DatabaseNotFound))]
    [InlineData("InvalidDatabaseName", nameof(ProtocolErrorCode.DatabaseNotFound))]
    [InlineData("AuthenticateResponseExpected", nameof(ProtocolErrorCode.ProtocolViolation))]
    [InlineData("AuthenticationFailed", nameof(ProtocolErrorCode.AuthenticationFailed))]
    [InlineData("DatabaseOffline", nameof(ProtocolErrorCode.Unavailable))]
    public async Task HandshakeRefused_EachRefusal_ShouldBeReportedOnceWithItsCode(string refusal, string code)
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(
            options => options.Authenticator = refusal == "AuthenticationFailed" ? new RejectingAuthenticator() : null,
            engineName: engineName);
        if (refusal == "DatabaseOffline")
        {
            var open = await harness.Engine.OpenDatabaseAsync(ServerTestHarness.DatabaseName);
            open.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "the test took the storage offline", new IOException("Injected device failure")).ShouldBeTrue();
        }

        await using var client = await harness.DialAsync();
        // A name the area's name rule refuses (#1392) names no database, so it is not found either.
        string database = refusal switch { "DatabaseNotFound" => "no-such-db", "InvalidDatabaseName" => "CON", _ => ServerTestHarness.DatabaseName };
        var version = refusal == "UnsupportedVersion" ? new ProtocolVersion(99, 0) : ProtocolVersion.Current;

        // Act
        if (refusal == "StartupExpected")
        {
            await client.SendAsync(ProtocolMessageType.Ping);
        }
        else
        {
            await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(version, database, "mallory").Encode());
            if (refusal is "AuthenticateResponseExpected" or "AuthenticationFailed" or "DatabaseOffline")
            {
                await client.ExpectAsync(ProtocolMessageType.Authenticate);
                await client.SendAsync(refusal == "AuthenticateResponseExpected" ? ProtocolMessageType.Ping : ProtocolMessageType.AuthenticateResponse);
            }
        }

        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        error.Code.ToString().ShouldBe(code);
        var refused = recorder.Events.Where(e => IsFor(e, 3, sessionId)).ShouldHaveSingleItem();
        refused.EventName.ShouldBe("HandshakeRefused");
        refused.Level.ShouldBe(EventLevel.Warning);
        refused.PayloadNames.ShouldBe(["sessionId", "database", "principal", "code", "detail"]);
        refused.Payload![1].ShouldBe(refusal == "StartupExpected" ? string.Empty : database);
        refused.Payload[2].ShouldBe(refusal == "StartupExpected" ? string.Empty : "mallory");
        refused.Payload[3].ShouldBe(code);
        refused.Payload[4].ShouldBe(error.Message);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeRefused");
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([3, 5]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a handshake that outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_SilentPeer_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(200), engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act: send nothing.
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        var timedOut = recorder.Events.Where(e => IsFor(e, 4, sessionId)).ShouldHaveSingleItem();
        timedOut.EventName.ShouldBe("HandshakeTimedOut");
        timedOut.Level.ShouldBe(EventLevel.Warning);
        timedOut.PayloadNames.ShouldBe(["sessionId", "timeoutMilliseconds"]);
        timedOut.Payload![1].ShouldBe(200d);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeTimedOut");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a handshake whose authenticator outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_StallingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange: the timeout lapses inside the authenticator, not at a read.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(
            options =>
            {
                options.Authenticator = new StallingAuthenticator();
                options.AuthenticationTimeout = TimeSpan.FromSeconds(2);
            },
            engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, ServerTestHarness.DatabaseName, "ada").Encode());
        await client.ExpectAsync(ProtocolMessageType.Authenticate);
        await client.SendAsync(ProtocolMessageType.AuthenticateResponse);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert: dropped without an error frame and reported as the timeout, not as a cancellation.
        recorder.Events.Where(e => IsFor(e, 4, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe(2000d);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeTimedOut");
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([4, 5]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should cut the names an unauthenticated peer sent to their bound in a handshake refusal")]
    public async Task HandshakeRefused_OversizedStartup_ShouldWriteBoundedNames()
    {
        // Arrange: a startup refused before any lookup, naming a database and principal far past
        // the 256-character payload bound.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(new ProtocolVersion(99, 0), new string('d', 100_000), new string('p', 100_000)).Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        var refused = recorder.Events.Where(e => IsFor(e, 3, sessionId)).ShouldHaveSingleItem();
        refused.Payload.ShouldBe([sessionId, new string('d', 256) + "...", new string('p', 256) + "...", nameof(ProtocolErrorCode.UnsupportedVersion), error.Message]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a database on a refused data-storage format as a handshake refusal once")]
    public async Task HandshakeRefused_RefusedStorageFormat_ShouldBeReportedOnceAsUnavailable()
    {
        // Arrange: a file-backed database whose catalog marker is forged to format 3, which the
        // engine refuses at open (#1099).
        string engineName = UniqueEngineName();
        string rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-events", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var creating = SqlDatabaseEngine.Create(engineName, new SqlDatabaseEngineOptions { RootPath = rootPath }))
            {
                var created = await creating.CreateDatabaseAsync("format-db");
                await created.Catalog.SetRecordSpaceFormatVersionAsync(3);
            }

            using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
            await using var engine = SqlDatabaseEngine.Create(engineName, new SqlDatabaseEngineOptions { RootPath = rootPath });
            await using var listener = new InMemoryConnectionListener();
            await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
            await server.StartAsync();
            IDuplexConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, TestTimeout.Token());
            await using var client = new ProtocolTestClient(connection);

            // Act
            await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "format-db", "ada").Encode());
            var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
            (await client.ReadAsync()).ShouldBeNull();
            Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
            await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

            // Assert
            error.Code.ShouldBe(ProtocolErrorCode.Unavailable);
            error.Message.ShouldContain("uses data-storage format 3");
            var refused = recorder.Events.Where(e => IsFor(e, 3, sessionId)).ShouldHaveSingleItem();
            refused.Payload.ShouldBe([sessionId, "format-db", "ada", nameof(ProtocolErrorCode.Unavailable), error.Message]);
            recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeRefused");
            recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([3, 5]);
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a session the idle timeout evicts as closed once")]
    public async Task SessionClosed_IdleTimeout_ShouldBeReportedOnceWithItsReason()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(options => options.IdleTimeout = TimeSpan.FromMilliseconds(200), engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: go idle.
        await client.ExpectAsync(ProtocolMessageType.Error);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        closed.Payload![1].ShouldBe("IdleTimeout");
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([5]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a protocol violation once and the session closed for it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionProtocolViolation_ReadyState_ShouldBeReportedOnce(bool malformedParameter)
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: a frame the ready state does not take, or an execute whose parameter fails to decode.
        if (malformedParameter)
        {
            var message = new ProtocolExecuteMessage("SELECT id FROM users", new Dictionary<string, byte[]> { ["broken"] = [] });
            await client.SendAsync(ProtocolMessageType.Execute, message.Encode());
        }
        else
        {
            await client.SendAsync(ProtocolMessageType.Authenticate);
        }

        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        var violation = recorder.Events.Where(e => IsFor(e, 6, sessionId)).ShouldHaveSingleItem();
        violation.EventName.ShouldBe("SessionProtocolViolation");
        violation.Level.ShouldBe(EventLevel.Warning);
        violation.PayloadNames.ShouldBe(["sessionId", "exceptionMessage"]);
        violation.Payload![1].ShouldBe(error.Message);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("ProtocolViolation");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report an unexpected fault in the exchange as an error once")]
    public async Task SessionFaulted_ThrowingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(options => options.Authenticator = new FaultingAuthenticator(), engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, ServerTestHarness.DatabaseName, "ada").Encode());
        await client.ExpectAsync(ProtocolMessageType.Authenticate);
        await client.SendAsync(ProtocolMessageType.AuthenticateResponse);
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert: the peer gets an internal error only; the event carries the failure.
        error.Code.ShouldBe(ProtocolErrorCode.Internal);
        error.Message.ShouldNotContain(FaultingAuthenticator.FaultMessage);
        var faulted = recorder.Events.Where(e => IsFor(e, 7, sessionId)).ShouldHaveSingleItem();
        faulted.EventName.ShouldBe("SessionFaulted");
        faulted.Level.ShouldBe(EventLevel.Error);
        faulted.PayloadNames.ShouldBe(["sessionId", "exceptionType", "exceptionMessage"]);
        faulted.Payload.ShouldBe([sessionId, typeof(InvalidOperationException).FullName, FaultingAuthenticator.FaultMessage]);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("Faulted");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report the sessions a lapsed drain aborts once")]
    public async Task SessionsAborted_DrainTimesOut_ShouldBeReportedOnce()
    {
        // Arrange: a session held inside its handshake, which the soft stop cannot end.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        var authenticator = new BlockingAuthenticator();
        await using var harness = await ServerTestHarness.StartAsync(
            options =>
            {
                options.Authenticator = authenticator;
                options.ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100);
            },
            engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, ServerTestHarness.DatabaseName, "ada").Encode());
        await client.ExpectAsync(ProtocolMessageType.Authenticate);
        await client.SendAsync(ProtocolMessageType.AuthenticateResponse);
        await authenticator.Entered.WaitAsync(TestTimeout.Token(30));

        // Act
        Task stop = harness.Server.StopAsync();
        var aborted = await recorder.WaitForAsync(e => e.EventId == 9 && Equals(e.Payload![0], engineName), TestTimeout.Token(30));
        authenticator.Release(authenticated: true);
        await stop.WaitAsync(TestTimeout.Token(30));
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        recorder.Events.Where(e => e.EventId == 9 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        aborted.EventName.ShouldBe("SessionsAborted");
        aborted.Level.ShouldBe(EventLevel.Warning);
        aborted.PayloadNames.ShouldBe(["engineName", "sessions", "drainTimeoutMilliseconds"]);
        aborted.Payload.ShouldBe([engineName, 1, 100d]);
        harness.Server.Sessions.ShouldBeEmpty();
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should publish its server-session counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishServerSessionCounters()
    {
        // Arrange
        string[] counters = ["current-server-sessions", "total-server-sessions", "total-rejected-sessions"];

        // Act
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, TestTimeout.Token(30));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should allocate nothing on any write while nobody listens")]
    public async Task Writes_NoListener_ShouldAllocateNothing()
    {
        // Arrange: a disabled source. Disposing the last listener already disables it; the explicit
        // disable below is redundant but harmless.
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var session = new StubServerSession();
        var failure = new InvalidOperationException("failure");
        using (var listener = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose))
        {
            listener.DisableEvents(SqlDatabaseEventSource.Log);
        }

        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        WriteEveryEvent(engine, session, failure);

        // Act: the fewest bytes of three rounds. A write that allocates does so in every round, while
        // the runtime can now and then charge an allocation of its own to this thread.
        long allocated = long.MaxValue;
        long timestamp = 0;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            timestamp |= WriteEveryEvent(engine, session, failure);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert: no round read a timestamp.
        allocated.ShouldBe(0);
        timestamp.ShouldBe(0);
    }

    private static long WriteEveryEvent(DatabaseEngine engine, DatabaseServerSession session, Exception failure)
    {
        var log = SqlDatabaseEventSource.Log;
        long timestamp = log.SessionTimestamp();
        log.SessionAccepted(engine, session, 1);
        log.SessionRejected(engine, SqlDatabaseEventSource.RejectReason.SessionLimit, 1, 1);
        log.HandshakeRefused(session, "app", "ada", ProtocolErrorCode.AuthenticationFailed, "refused");
        log.HandshakeTimedOut(session, TimeSpan.FromSeconds(1));
        log.SessionProtocolViolation(session, "violation");
        log.SessionFaulted(session, failure);
        log.SessionCleanupFailed(session, failure);
        log.SessionClosed(session, SqlDatabaseEventSource.CloseReason.Terminated, timestamp);
        log.SessionsAborted(engine, 1, TimeSpan.FromSeconds(1));
        return timestamp;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should close a session whose peer reset its TCP connection with a transport reason, not a fault")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionClosed_PeerResetsTcpConnection_ShouldReportATransportReasonAndNoFault(bool whileServerWrites)
    {
        // Arrange: a real TCP listener; the in-memory driver cannot reset a connection.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var engine = SqlDatabaseEngine.Create(engineName, new SqlDatabaseEngineOptions());
        await engine.CreateDatabaseAsync(ServerTestHarness.DatabaseName);
        TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(TestTimeout.Token());

        // Act: the peer completes its handshake, then resets the connection: while the session idles
        // in its ready loop, or while the server's pong writes fill the socket buffers the peer
        // never drains, so the reset fails a send the pump is blocked in.
        using (Socket socket = await TcpResetPeer.ConnectReadyAsync(listener.EndPoint, ServerTestHarness.DatabaseName, TestTimeout.Token()))
        {
            Task? flood = whileServerWrites ? TcpResetPeer.FloodPingsAsync(socket, 4_000_000) : null;
            if (flood is not null)
            {
                await TcpResetPeer.WaitUntilServerStalledAsync(socket, TimeSpan.FromSeconds(15));
            }

            TcpResetPeer.Reset(socket);
            if (flood is not null)
            {
                // Whether the kernel took the whole flood before the reset does not matter.
                await Record.ExceptionAsync(() => flood);
            }
        }

        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert: a peer that hung up is an expected outcome (plan D9), never a server fault. The
        // TCP driver reports a reset its receive sees as the stream's end, so an idle session
        // closes as PeerClosed; a reset that fails a send closes it as TransportFailed. The write
        // case accepts only the transport reasons: a PeerClosed there means the flood no longer
        // blocked the pump in a send, and the case stopped testing the classification.
        string seen = string.Join("; ", recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventName + "(" + string.Join(", ", e.Payload!.Skip(1)) + ")"));
        recorder.Events.Where(e => IsFor(e, 7, sessionId)).ShouldBeEmpty("The pump reported the reset as a fault: " + seen);
        string[] expected = whileServerWrites
            ? ["TransportFailed", "ConnectionAborted"]
            : ["PeerClosed", "TransportFailed", "ConnectionAborted", "Cancelled"];
        ((string)closed.Payload![1]!).ShouldBeOneOf(expected, seen);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should close a session once and restore the gauge when its connection's disposal throws")]
    public async Task SessionClosed_ConnectionDisposalThrows_ShouldStillCloseOnceAndRestoreTheGauge()
    {
        // Arrange: every accepted connection throws from its disposal.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var inner = new InMemoryConnectionListener();
        var listener = new DisposeFailingConnectionListener(inner);
        await using var harness = await ServerTestHarness.StartAsync(options => options.Listener = listener, engineName: engineName);
        long currentBefore = SqlDatabaseEventSource.Log.CurrentServerSessions;

        // Act: a session that terminates cleanly, whose cleanup then meets the failing disposal.
        await using (var client = new ProtocolTestClient(await inner.CreateFactory().ConnectAsync(inner.EndPoint, TestTimeout.Token())))
        {
            await client.HandshakeAsync();
            await client.SendAsync(ProtocolMessageType.Terminate);
            Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
            await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));
            await ServerTestHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);

            // Assert: the session left the server once, whatever its disposal threw.
            recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("Terminated");
        }

        SqlDatabaseEventSource.Log.CurrentServerSessions.ShouldBe(currentBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private static string UniqueEngineName() => "sql-events-" + Guid.NewGuid().ToString("N");

    private static async Task<Guid> AcceptedSessionAsync(EventSourceRecorder recorder, string engineName)
    {
        var accepted = await recorder.WaitForAsync(e => e.EventId == 1 && Equals(e.Payload![0], engineName), TestTimeout.Token(30));
        return (Guid)accepted.Payload![1]!;
    }

    // The session events whose first payload field is the session's id.
    private static bool IsForSession(EventWrittenEventArgs written, Guid sessionId)
        => written.EventId is >= 3 and <= 8 && written.Payload is { Count: > 0 } payload && Equals(payload[0], sessionId);

    private static bool IsFor(EventWrittenEventArgs written, int eventId, Guid sessionId)
        => written.EventId == eventId && IsForSession(written, sessionId);

    /// <summary>A server session with no pump, for the writes the allocation check makes.</summary>
    private sealed class StubServerSession : DatabaseServerSession
    {
        public override DatabaseSession? DatabaseSession => null;

        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
