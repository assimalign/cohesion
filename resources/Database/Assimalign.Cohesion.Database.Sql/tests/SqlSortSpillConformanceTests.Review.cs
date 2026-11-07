using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Spill cancellation isolates one enumeration and permits rewind (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRowsAsync_CancelOneReader_ShouldLeaveOtherReadersAndResultUsable(bool alreadyCancelled)
    {
        // Arrange: deliberately no await using for the result whose lifetime is tested.
        await using var engine = await Create("cancel-reader", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k");
        using var cancellation = new CancellationTokenSource();
        var first = result.GetRowsAsync(cancellation.Token).GetAsyncEnumerator();
        var second = result.GetRowsAsync().GetAsyncEnumerator();
        try
        {
            (await second.MoveNextAsync()).ShouldBeTrue();
            if (!alreadyCancelled) { (await first.MoveNextAsync()).ShouldBeTrue(); }

            // Act
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await first.MoveNextAsync());

            // Assert
            int count = 1;
            while (await second.MoveNextAsync()) { count++; }
            count.ShouldBe(4000);
            (await Rows(result)).Length.ShouldBe(4000);
            TempBytes(engine.TempSpace!).ShouldBeGreaterThan(0);
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); await result.DisposeAsync(); }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill damage is terminal and coded for concurrent and later readers (#1789)")]
    [InlineData("CK05004")]
    [InlineData("CK05005")]
    public async Task GetRowsAsync_ConcurrentReadFailure_ShouldRetainDiagnosticForEveryReader(string code)
    {
        // Arrange
        await using var engine = await Create("terminal-read", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k");
        var first = result.GetRowsAsync().GetAsyncEnumerator();
        var second = result.GetRowsAsync().GetAsyncEnumerator();
        (await first.MoveNextAsync()).ShouldBeTrue(); (await second.MoveNextAsync()).ShouldBeTrue();
        BeforeRead(engine.TempSpace!, () => throw TempException(code, "injected read failure"));
        try
        {
            // Act / Assert
            var failure = await Should.ThrowAsync<DatabaseException>(async () => { while (await first.MoveNextAsync()) { } });
            failure.Diagnostics[0].Code.ShouldBe(code);
            var concurrent = await Should.ThrowAsync<DatabaseException>(async () => await second.MoveNextAsync());
            concurrent.Diagnostics[0].Code.ShouldBe(code);
            var later = await Should.ThrowAsync<DatabaseException>(async () => await Rows(result));
            later.Diagnostics[0].Code.ShouldBe(code);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { BeforeRead(engine.TempSpace!, null); await first.DisposeAsync(); await second.DisposeAsync(); await result.DisposeAsync(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Eight undisposed spilled results bound a session and disposal frees a slot (#1789)")]
    public async Task ExecuteAsync_UndisposedResults_ShouldRefuseNinthUntilSlotReleased()
    {
        // Arrange
        await using var engine = await Create("result-cap", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        var results = new List<DatabaseResultSet>();
        try
        {
            for (int i = 0; i < 8; i++) { results.Add((DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k")); }
            long live = TempBytes(engine.TempSpace!);

            // Act / Assert
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k"));
            error.Diagnostics[0].Code.ShouldBe("CS05067"); error.Message.ShouldContain("undisposed");
            TempBytes(engine.TempSpace!).ShouldBe(live);
            await results[0].DisposeAsync();
            results.Add((DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k"));
            (await Rows(results[^1])).Length.ShouldBe(4000);
        }
        finally { foreach (var result in results) { await result.DisposeAsync(); } }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Active merge reader bytes share one grant and refused readers leave the result intact (#1789)")]
    public async Task GetRowsAsync_ReaderWorkspace_ShouldRefuseAboveGrantAndRecover()
    {
        // Arrange: same fan-in arithmetic as 64 MiB, scaled to a 256 KiB grant.
        const long grant = 262144;
        await using var engine = await Create("reader-grant", grant);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        var result = (SqlSpilledResultSet)await session.ExecuteAsync("SELECT id AS marker,reader_payload(id) AS value FROM t ORDER BY marker");
        long decoded = SqlTempRowCodec.DecodedBytes([0, new string('a',5500)]);
        long bytes = Sorted(result)!.BufferBytes + decoded;
        Workspace(Sorted(result)!).Length.ShouldBe(0);
        var readers = new List<IAsyncEnumerator<DatabaseRow>>();
        try
        {
            for (long live = 0; live + bytes + decoded <= grant; live += bytes)
            {
                var cursor = result.GetRowsAsync().GetAsyncEnumerator(); readers.Add(cursor);
                (await cursor.MoveNextAsync()).ShouldBeTrue();
            }
            long liveBytes = readers.Count * bytes;
            readers.Count.ShouldBe(2);
            const long defaultGrant = 67108864, defaultMaximumRecord = 4184064;
            long defaultReader = 8 * (8192 + defaultMaximumRecord) + decoded;
            ((defaultGrant - decoded) / defaultReader).ShouldBe(1);
            _output.WriteLine($"READER_DEFAULT_ARITHMETIC|grant={defaultGrant}|record={defaultMaximumRecord}|per_reader={defaultReader}|decode_overlap={decoded}|reader_capacity=1");
            ReaderBytes(result).ShouldBe(liveBytes);
            var refused = result.GetRowsAsync().GetAsyncEnumerator();

            // Act / Assert
            var error = await Should.ThrowAsync<DatabaseException>(async () => await refused.MoveNextAsync());
            error.Diagnostics[0].Code.ShouldBe("CS05067"); error.Message.ShouldContain((liveBytes + decoded).ToString());
            await refused.DisposeAsync();
            ReaderBytes(result).ShouldBe(liveBytes);
            foreach (var cursor in readers) { (await cursor.MoveNextAsync()).ShouldBeTrue(); }
            await readers[0].DisposeAsync(); readers.RemoveAt(0);
            var replacement = result.GetRowsAsync().GetAsyncEnumerator(); readers.Add(replacement);
            (await replacement.MoveNextAsync()).ShouldBeTrue();
            _output.WriteLine($"READER_BYTES|grant={grant}|per_reader={bytes}|readers={readers.Count}|live={ReaderBytes(result)}");
        }
        finally { foreach (var cursor in readers) { await cursor.DisposeAsync(); } await result.DisposeAsync(); }
        ReaderBytes(result).ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Empty windows preserve call order across collector, staged, deferred, grouped and DISTINCT paths (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EmptyWindowMatrix_ShouldMatchCallLog(bool pastEnd)
    {
        // Arrange
        var logs = new[] { new List<long>(), new List<long>() };
        await using var reference = await Create("window-reference", 67108864, logs[0]);
        await using var engine = await Create("window-spill", 1048576, logs[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a); await Seed(b);
        string window = pastEnd ? " OFFSET 20000" : " LIMIT 0";
        string[] shapes =
        [
            "SELECT stamp(id) AS v FROM t ORDER BY v",
            "SELECT stamp(t.id) AS v FROM t CROSS JOIN u ORDER BY v",
            "SELECT stamp(t.id) FROM t CROSS JOIN u ORDER BY t.id",
            "SELECT stamp(id%1000) AS v,wide(id%1000) AS w,COUNT(*) FROM t GROUP BY id%1000 HAVING COUNT(*)>0 ORDER BY w,v",
            "SELECT DISTINCT stamp(k) FROM t ORDER BY id",
        ];
        foreach (string shape in shapes)
        {
            logs[0].Clear(); logs[1].Clear();
            // Act
            await using var left = (DatabaseResultSet)await a.ExecuteAsync(shape + window);
            await using var right = (DatabaseResultSet)await b.ExecuteAsync(shape + window);
            // Assert
            (await Rows(right)).ShouldBe(await Rows(left), customMessage: shape + window);
            logs[1].ShouldBe(logs[0], customMessage: shape + window);
            left.ShouldBeOfType<SqlMaterializedResultSet>();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Temp close failures in DISTINCT remain CK05005 and preserve an explicit transaction (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CloseDuringEvaluation_ShouldKeepTransactionUsable(bool deferred)
    {
        // Arrange
        await using var engine = await Create("close-transaction", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await Seed(session); await session.ExecuteAsync("BEGIN");
        bool injected = false;
        BeforeRead(engine.TempSpace!, () =>
        {
            if (injected) { return; }
            var run = StatementRuns(engine.TempSpace!).First();
            RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle); injected = true;
        });
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(deferred
                ? "SELECT stamp(t.id),t.k FROM t CROSS JOIN u ORDER BY t.id"
                : "SELECT DISTINCT note FROM t ORDER BY id"));
            // Assert
            injected.ShouldBeTrue(); failure.Diagnostics[0].Code.ShouldBe("CK05005");
            (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { BeforeRead(engine.TempSpace!, null); await session.ExecuteAsync("ROLLBACK"); }
        (await session.ExecuteAsync("SELECT id FROM t LIMIT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Cancelling staged sorting releases its entire statement (#1789)")]
    public async Task ExecuteAsync_CancelStagedSort_ShouldReleaseStatement()
    {
        // Arrange
        await using var engine = await Create("staged-cancel", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        using var cancellation = new CancellationTokenSource(); bool exercised = false;
        SqlRowCollector.ObserveSpill = phase => { if (phase == "staged-sort") { exercised = true; cancellation.Cancel(); } };
        try
        {
            // Act / Assert
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "SELECT stamp(t.id) AS v FROM t CROSS JOIN u ORDER BY v", cancellationToken: cancellation.Token));
            exercised.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Live samples reach staged input, output spool, Transform and Deduplicate (#1789)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ExecuteAsync_AdditionalLiveStages_ShouldFitGrant(int shape)
    {
        // Arrange
        const long grant = 1048576, constant = 524288;
        await using var engine = await Create("live-stage", grant);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        if (shape == 0) { await session.ExecuteAsync("UPDATE t SET note=@note", new Dictionary<string, object?> { ["note"] = new string('x',1000) }); }
        string sql = shape switch
        {
            0 => "SELECT stamp(t.id) AS v FROM t CROSS JOIN u CROSS JOIN u v ORDER BY v",
            1 => "SELECT wide(id) FROM t WHERE id<500 ORDER BY id",
            2 => "SELECT stamp(id) FROM t WHERE id<1000 ORDER BY wide(id)",
            _ => "SELECT DISTINCT wide(id%7) AS v FROM t ORDER BY id",
        };
        string required = shape switch { 0 => "staged-drained", 1 => "output-spool", 2 => "transform", _ => "deduplicate" };
        await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
        long baseline = LiveHeap.Read(); var samples = new Dictionary<string, long>();
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != required && !(shape == 0 && phase == "staged-sort")) { return; }
            samples[phase] = Math.Max(samples.GetValueOrDefault(phase),Math.Max(0, LiveHeap.Read()-baseline));
        };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
            await foreach (var row in result.GetRowsAsync()) { _ = row.FieldCount; }
            // Assert
            samples.ShouldContainKey(required);
            foreach (var sample in samples)
            {
                _output.WriteLine($"LIVE_STAGE|{sample.Key}|bytes={sample.Value}|grant={grant}|constant={constant}");
                sample.Value.ShouldBeLessThanOrEqualTo((long)(grant*1.1)+constant);
            }
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Nested loop inner lists are collectible after input is drained (#1789)")]
    public async Task ExecuteAsync_CompletedNestedLoop_ShouldReleaseInnerLists()
    {
        // Arrange
        await using var engine = await Create("inner-weak", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session); var weak = new List<WeakReference>(); bool observed = false;
        SqlRowCollector.ObserveBuffer = (phase, buffer) => { if (phase == "join-inner") { weak.Add(new WeakReference(buffer)); } };
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != "staged-sort") { return; }
            LiveHeap.Read(); weak.ShouldNotBeEmpty(); weak.Count(item => item.IsAlive).ShouldBe(0); observed = true;
        };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT stamp(t.id) AS v FROM t CROSS JOIN u ORDER BY v");
            // Assert
            observed.ShouldBeTrue();
        }
        finally { SqlRowCollector.ObserveBuffer = null; SqlRowCollector.ObserveSpill = null; }
    }

    private static IEnumerable<StorageTempRun> StatementRuns(StorageTempSpace space)
        => Statements(space).SelectMany(statement => StatementRunSet(statement));
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_statements")]
    private static extern ref HashSet<StorageTempStatement> Statements(StorageTempSpace space);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runs")]
    private static extern ref HashSet<StorageTempRun> StatementRunSet(StorageTempStatement statement);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_workspace")]
    private static extern ref Memory<byte> Workspace(StorageTempSortReader reader);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_readerBytes")]
    private static extern ref long ReaderBytes(SqlSpilledResultSet result);
}
