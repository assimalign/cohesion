using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapMeasurement;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    private static int _outputPrefixAlive;

    [Theory(DisplayName = "Cohesion Test [Sql] - Nested spill permission preserves timestamp, schema, database and metadata timeout (#1789)")]
    [InlineData(null, false)]
    [InlineData("app", false)]
    [InlineData("app", true)]
    public async Task WithSession_ExcludedOwner_ShouldPreserveStatementIdentity(string? schema, bool checkpoint)
    {
        // Arrange
        await using var engine = await Create("output-permission", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, default);
        var timestamp = new DateTimeOffset(2026, 11, 2, 12, 30, 0, TimeSpan.FromHours(-5));
        var timeout = schema is null ? TimeSpan.Zero : TimeSpan.FromSeconds(7);
        var original = new SqlStatementContext(transaction, database.Coordinator, schema, "db",
            transactionStart: timestamp, metadataLockTimeout: timeout, checkpointCopy: checkpoint);
        try
        {
            // Act
            var enabled = original.WithSession(session);
            var excluded = enabled.WithoutSession();
            var restored = excluded.WithSession(session);

            // Assert
            enabled.Session.ShouldBeSameAs(session);
            excluded.Session.ShouldBeNull(); restored.Session.ShouldBeSameAs(session);
            foreach (var context in new[] { original, enabled, excluded, restored })
            {
                context.TransactionStart.ShouldBe(timestamp.ToUniversalTime());
                context.ProvisioningSchema.ShouldBe(schema); context.DatabaseName.ShouldBe("db");
                context.MetadataLockTimeout.ShouldBe(timeout); context.CheckpointCopy.ShouldBe(checkpoint);
                context.Metrics.ShouldBeSameAs(original.Metrics);
            }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, default); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unordered result spooling preserves the first evaluator error, calls and visits (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OutputExpressionFailure_ShouldPreserveEvaluation(bool tableAs)
    {
        // Arrange
        var calls = new[] { new List<long>(), new List<long>() };
        var failures = new List<(string? Code, string Message, long Examined, string Calls)>();
        for (int index = 0; index < 2; index++)
        {
            await using var engine = await Create("output-error-" + index, index == 0 ? 67108864 : 262144, calls[index]);
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await Seed(session);
            string sql = (tableAs ? "CREATE TABLE archive AS " : "") +
                "SELECT fails_on(id) AS id,RPAD(note,1024,'x') AS note FROM t";

            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));

            // Assert
            var metrics = session.LastStatementMetrics!;
            failures.Add((failure.Diagnostics[0].Code, failure.Message, metrics.RecordsExamined, string.Join(',', calls[index])));
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        failures[1].ShouldBe(failures[0]); calls[0].ShouldBe(Enumerable.Range(0, 318).Select(i => (long)i));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Output spool cancellation releases each execution and delivery boundary (#1789)")]
    [InlineData("output-drained")]
    [InlineData("output-append")]
    [InlineData("output-spool")]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("delivery")]
    public async Task ExecuteAsync_OutputCanceled_ShouldReleaseEveryOwner(string point)
    {
        // Arrange
        await using var engine = await Create("output-cancel", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        using var cancellation = new CancellationTokenSource();
        bool exercised = false;
        void Cancel() { exercised = true; cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
        SqlRowCollector.ObserveSpill = phase => { if (phase == point) { Cancel(); } };
        if (point == "create") { BeforeCreate(engine.TempSpace!, Cancel); }
        if (point == "write") { BeforeWrite(engine.TempSpace!, Cancel); }
        DatabaseResultSet? result = null;
        try
        {
            // Act / Assert
            await Should.ThrowAsync<OperationCanceledException>(async () =>
            {
                result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t", cancellationToken: cancellation.Token);
                if (point == "delivery") { BeforeRead(engine.TempSpace!, Cancel); }
                await Rows(result, cancellation.Token);
            });
            exercised.ShouldBeTrue();
            if (result is not null) { await result.DisposeAsync(); }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally
        {
            SqlRowCollector.ObserveSpill = null; BeforeCreate(engine.TempSpace!, null);
            BeforeWrite(engine.TempSpace!, null); BeforeRead(engine.TempSpace!, null);
            if (result is not null) { await result.DisposeAsync(); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unordered output quota refusal releases files, charges and CTAS reservation (#1789)")]
    [InlineData("SELECT id,RPAD(note,1024,'x') FROM t")]
    [InlineData("CREATE TABLE archive AS SELECT id,RPAD(note,1024,'x') AS note FROM t")]
    [InlineData("WITH d AS (SELECT id,RPAD(note,1024,'x') AS note FROM t) SELECT * FROM d")]
    public async Task ExecuteAsync_OutputQuotaRefusal_ShouldReleaseAndKeepDatabaseOnline(string sql)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("output-quota");
        builder.Options.RootPath = Path.Combine(_root, "output-quota");
        builder.Options.StatementMemoryBudget = 262144; builder.Options.StatementTempSpaceQuota = 0;
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK05002");
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); database.IsOffline.ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
        await using var count = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM t");
        (await Rows(count)).ShouldHaveSingleItem().Cells[0].ShouldBe(4000L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Output spool is lazy and rewindable, and early reader/result disposal releases files (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRowsAsync_OutputEarlyExit_ShouldReleaseOnResultOrSessionClose(bool closeSession)
    {
        // Arrange
        await using var engine = await Create("output-early", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        var result = (await session.ExecuteAsync("SELECT id,k FROM t")).ShouldBeOfType<SqlSpilledResultSet>();
        int reads = 0; BeforeRead(engine.TempSpace!, () => reads++);
        try
        {
            // Act
            await using (var reader = result.GetRowsAsync().GetAsyncEnumerator())
            {
                reads.ShouldBe(0);
                (await reader.MoveNextAsync()).ShouldBeTrue(); reader.Current.GetInt32(0).ShouldBe(0);
            }
            reads.ShouldBeGreaterThan(0);
            await using (var reader = result.GetRowsAsync().GetAsyncEnumerator())
            { (await reader.MoveNextAsync()).ShouldBeTrue(); reader.Current.GetInt32(0).ShouldBe(0); }
            if (closeSession) { await session.DisposeAsync(); } else { await result.DisposeAsync(); }

            // Assert
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            await Should.ThrowAsync<ObjectDisposedException>(async () => await Rows(result));
        }
        finally { BeforeRead(engine.TempSpace!, null); await result.DisposeAsync(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Output spool weak references reject a kept prefix and live growth rejects per-row retention (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_OutputLiveHeap_ShouldRejectKeptPrefixAndPerRowRetention(bool mutant)
    {
        // Arrange: minimum of three executions per population in the same warmed engine.
        const long grant = 1048576;
        await using var engine = await Create("output-live", grant);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var populations = new List<long>();
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        foreach (int count in new[] { 10000, 30000 })
        {
            long minimum = long.MaxValue;
            for (int execution = -1; execution < 3; execution++)
            {
                long live = Measure(pool, _output, nameof(Add_OutputLiveHeap_ShouldRejectKeptPrefixAndPerRowRetention), $"rows={count}|mutant={mutant}|execution={execution}", readHeap =>
                {
                    images.Drain(pool);
                    long before = readHeap();
                    long during = MeasureOutputLiveRows(count, session, mutant && execution >= 0, grant, readHeap: readHeap);
                    long after = readHeap();
                    // The helper has released its objects before the second quiescent reading.
                    // The lower baseline prevents ambient reclamation from hiding retention.
                    long live = Math.Max(0, during - Math.Min(before, after));
                    _output.WriteLine($"OUTPUT_LIVE_ROUND|rows={count}|mutant={mutant}|execution={execution}|baseline_before={before}|during={during}|baseline_after={after}|live={live}");
                    return live;
                });
                if (execution >= 0) { minimum = Math.Min(minimum, live); }
            }
            minimum.ShouldBeLessThanOrEqualTo(grant + 524288);
            populations.Add(minimum);
            _output.WriteLine($"OUTPUT_LIVE|rows={count}|mutant={mutant}|live={minimum}");
        }
        void AssertGrowth() => (populations[1] - populations[0]).ShouldBeLessThanOrEqualTo(65536);
        if (mutant) { Should.Throw<ShouldAssertException>(AssertGrowth); }
        else { AssertGrowth(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureOutputLiveRows(int count, SqlDatabaseSession owner, bool mutant, long grant, bool keptPrefix = false, Func<long>? readHeap = null)
    {
        _outputPrefixAlive = -1;
        var retained = mutant ? new List<object>() : null;
        var prefix = keptPrefix ? new List<object>() : null;
        var weak = new List<WeakReference>();
        var metrics = new SqlStatementMetrics();
        using var collector = new SqlRowCollector([], null, null, null, default, metrics, grant);
        int drained = 0;
        SqlRowCollector.ObserveBuffer = (phase, row) => { if (phase == "output-drain") { prefix?.Add(row); } };
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != "output-drained") { return; }
            drained++;
            (readHeap ?? LiveHeap.Read)();
            // Drained arrays and payloads must die before Take, even if a retained prefix fits G.
            _outputPrefixAlive = weak.Count(reference => reference.IsAlive);
            _outputPrefixAlive.ShouldBe(0);
        };
        try
        {
            // Act
            for (int i = 0; i < count; i++) { AddOutputVector(collector, i, owner, weak, retained); }
        }
        finally { SqlRowCollector.ObserveSpill = null; SqlRowCollector.ObserveBuffer = null; GC.KeepAlive(prefix); }
        drained.ShouldBe(1);
        var result = collector.TakeSpilled([], 0, -1);
        try
        {
            long live = (readHeap ?? LiveHeap.Read)();
            // Assert: retaining boxed values cannot keep their row arrays or wide payloads alive.
            weak.Count(reference => reference.IsAlive).ShouldBe(0);
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
            metrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(grant); // ledger admission only
            GC.KeepAlive(retained);
            return live;
        }
        finally { result.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddOutputVector(SqlRowCollector collector, int value, SqlDatabaseSession owner,
        List<WeakReference> weak, List<object>? mutant)
    {
        object?[] row = [value, new string('x', 512)];
        if (value < 128) { weak.Add(new WeakReference(row)); weak.Add(new WeakReference(row[1])); }
        mutant?.Add(row[0]!); collector.Add(row, [], owner);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Output spool weak-reference check rejects a kept prefix before Take (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_KeptOutputPrefix_ShouldFailWeakReferenceCheck(bool mutant)
    {
        // Arrange
        const long grant = 1048576;
        await using var engine = await Create("kept-prefix", grant);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        // Act / Assert: the mutant holds the actual drained arrays and their payloads.
        if (mutant)
        {
            Should.Throw<ShouldAssertException>(() => MeasureOutputLiveRows(10000, session, false, grant, true));
            _outputPrefixAlive.ShouldBe(256);
        }
        else
        {
            MeasureOutputLiveRows(10000, session, false, grant).ShouldBeGreaterThan(0);
            _outputPrefixAlive.ShouldBe(0);
        }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        _output.WriteLine($"OUTPUT_PREFIX|mutant={mutant}|alive={_outputPrefixAlive}|weakCheck={(mutant ? "rejected" : "passed")}");
    }
}
