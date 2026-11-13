using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [L3bColdFact(DisplayName = "Cohesion Test [Sql] - A fresh process samples one cold combined spill shape (#1789)")]
    public async Task ExecuteAsync_JoinFix2ColdProcess_ShouldUseOneAllowance()
    {
        // Arrange: the evidence runner selects one shape before launching the process.
        string shape = Environment.GetEnvironmentVariable("L3B_COLD_SHAPE")!;
        shape.ShouldBeOneOf("group", "deferred", "ownership");
        // Act / Assert
        await ExecuteAsync_JoinFixCombinedHeap_ShouldUseOneAllowance(shape, cold: true);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Indexed join component charges bound two marginal live populations (#1789)")]
    [InlineData("directory")]
    [InlineData("cache")]
    [InlineData("sorter")]
    [InlineData("reader")]
    [InlineData("writer")]
    [InlineData("staged-reader")]
    [InlineData("deferred-writer")]
    [InlineData("operator")]
    [InlineData("staged-operator")]
    [InlineData("collector-operator")]
    public async Task JoinFixCharges_TwoPopulations_ShouldBoundComponent(string component)
    {
        // Arrange
        await using var engine = await Create("join-components", 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await MeasureJoinComponent(session, component, 8, 32); // warm factories and metadata
        // Act
        (long Live, long Charge) measured = (long.MaxValue, 0);
        for (int round = 1; round <= 3; round++)
        {
            var current = await MeasureJoinComponent(session, component, 128, 512);
            _output.WriteLine($"JOIN_COMPONENT_ROUND|component={component}|round={round}|live={current.Live}|charged={current.Charge}");
            if (current.Live < measured.Live) { measured = current; }
        }
        // Assert: no byte tolerance. Mutation runs halve the actual product charge.
        _output.WriteLine($"JOIN_COMPONENT|{component}|live={measured.Live}|charged={measured.Charge}|units=384");
        measured.Live.ShouldBeGreaterThan(0);
        measured.Live.ShouldBeLessThanOrEqualTo(measured.Charge);
        Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(long Live, long Charge)> MeasureJoinComponent(SqlDatabaseSession owner, string component, int small, int large)
    {
        var roots = new object?[large];
        var cleanup = new List<IDisposable>(large);
        var metrics = new SqlStatementMetrics();
        var keys = new SqlHashJoinPath([0], [0], [Collation.Binary]);
        var spillMetrics = new SqlStatementMetrics[large];
        var emptyTables = new SqlHashJoinTable[large];
        for (int i = 0; i < large; i++)
        {
            spillMetrics[i] = new SqlStatementMetrics();
            emptyTables[i] = new SqlHashJoinTable(keys, spillMetrics[i], long.MaxValue);
        }
        var statement = SqlHashSpillMetrics.JoinTemp(metrics, owner);
        using var run = statement.CreateRun(); run.Append(new byte[100]); run.Seal();
        using var staged = new SqlStagedSortSource(owner, metrics, long.MaxValue, 262144);
        staged.Append([1, new string('x', 256)], CancellationToken.None); staged.Seal(CancellationToken.None);
        var writers = new StorageTempRun[large];
        if (component is "writer" or "deferred-writer")
        {
            for (int i = 0; i < large; i++) { writers[i] = statement.CreateRun(); cleanup.Add(writers[i]); }
        }
        long unit = 0;
        try
        {
            Fill(0, small);
            long baseline = LiveHeap.Read();
            Fill(small, large);
            long live = LiveHeap.Read() - baseline;
            GC.KeepAlive(roots); GC.KeepAlive(cleanup);
            return (live, unit * (large - small));
        }
        finally
        {
            foreach (var value in cleanup) { value.Dispose(); }
            for (int i = 0; i < large; i++)
            { emptyTables[i].Dispose(); SqlHashSpillMetrics.DetachJoinTemp(spillMetrics[i])?.Dispose(); }
            SqlHashSpillMetrics.DetachJoinTemp(metrics)?.Dispose();
        }

        void Fill(int first, int last)
        {
            for (int i = first; i < last; i++)
            {
                if (component == "directory")
                {
                    roots[i] = new SqlHashJoinSpill.Chunk[128];
                    unit = SqlHashJoinSpill.DirectoryBytes(128);
                }
                else if (component == "cache")
                {
                    var rows = new SqlHashJoinSpill.BuildRecord[16];
                    unit = 24 + 128;
                    for (int row = 0; row < 16; row++)
                    {
                        object?[] values = [row, new string('x', 128)];
                        rows[row] = new(0, row, true, values); unit += SqlHashJoinSpill.CachedRecordBytes(values);
                    }
                    var chunk = new SqlHashJoinSpill.CachedChunk(0, rows, unit);
                    roots[i] = new LinkedListNode<SqlHashJoinSpill.CachedChunk>(chunk);
                }
                else if (component == "sorter")
                {
                    long grant = SqlHashJoinSpill.BuildSorterGrant(6144);
                    unit = SqlHashJoinSpill.SorterCharge(grant);
                    var sorter = StorageTempSorter.Create(statement, grant, 6144, 64);
                    roots[i] = sorter; cleanup.Add(sorter);
                }
                else if (component == "reader")
                { var reader = run.OpenReader(8192); roots[i] = reader; cleanup.Add(reader); unit = SqlHashJoinSpill.ReaderBytes; }
                else if (component is "writer" or "deferred-writer")
                {
                    // Allocate file/handle/registry objects before the first sample; only
                    // page retention is marginal, as those objects have their own charge.
                    var writer = writers[i]; writer.Append([1]); roots[i] = writer;
                    unit = component == "writer" ? SqlStagedSortSource.WriterBytes : SqlRowCollector.DeferredWriterBytes;
                }
                else if (component == "staged-reader")
                {
                    var reader = staged.Read(CancellationToken.None).GetEnumerator();
                    reader.MoveNext().ShouldBeTrue(); roots[i] = reader; cleanup.Add(reader); unit = staged.ReadReservation;
                }
                else if (component == "staged-operator")
                {
                    var source = new SqlStagedSortSource(owner, metrics, long.MaxValue, 262144);
                    source.ReserveWriter(); source.Seal(CancellationToken.None);
                    roots[i] = source; cleanup.Add(source); unit = SqlStagedSortSource.OperatorBytes;
                }
                else if (component == "collector-operator")
                {
                    var columns = new[] { new SqlTempSortKeyCodec.Column(false, true, Collation.Binary) };
                    var state = new SqlRowCollector.Spill(owner, owner.Database.Engine.TempSpace!, columns, 16384, 262144, statement);
                    state.Deferred = statement.CreateRun();
                    roots[i] = state; cleanup.Add(state); unit = SqlRowCollector.SpillObjectCharge(1);
                }
                else
                {
                    var spill = new SqlHashJoinSpill(owner, keys, spillMetrics[i], 262144, false);
                    spill.AdoptBuild(emptyTables[i], CancellationToken.None);
                    foreach (int unused in spill.Probe([1], CancellationToken.None)) { }
                    roots[i] = spill; cleanup.Add(spill); unit = SqlHashJoinSpill.OperatorBytes;
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL joins, groups and deferred collectors share one live allowance (#1789)")]
    [InlineData("group", true)]
    [InlineData("group", false)]
    [InlineData("deferred", true)]
    [InlineData("deferred", false)]
    [InlineData("ownership", true)]
    [InlineData("ownership", false)]
    [InlineData("yield", true)]
    public async Task ExecuteAsync_JoinFixCombinedHeap_ShouldUseOneAllowance(string shape, bool cold)
    {
        // Arrange
        long budget = shape == "yield" ? 262144 : 1048576;
        await using var engine = await Create("join-combined", budget);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        if (shape == "yield") { await SeedYieldJoin(session); }
        else
        {
            await SeedJoinFix(session, shape == "ownership" ? 5000 : 3000);
            await session.ExecuteAsync("CREATE TABLE jt(id INT,payload TEXT)");
            for (int start = 1000; start < 4000; start += 100)
            {
                var values = new List<string>();
                for (int i = start; i < start + 100; i++) { values.Add($"({i},'{new string('y', 512)}')"); }
                await session.ExecuteAsync("INSERT INTO jt VALUES" + string.Join(',', values));
            }
        }
        string sql = shape switch
        {
            "yield" => "SELECT jl.id,jr.id FROM jl FULL JOIN jr ON jl.id=jr.id",
            "deferred" => "SELECT l.id,r.payload,t.id FROM jl l FULL JOIN jr r ON l.id=r.id FULL JOIN jt t ON r.id=t.id ORDER BY t.id IS NULL LIMIT 600",
            "ownership" => $"SELECT l.id/4 g,BOOL_AND(r.payload IS NULL) b,CAST(l.id/4 AS TEXT)||'{new string('z', 1000)}' pad FROM jl l LEFT JOIN jr r ON l.id=r.id GROUP BY l.id/4 ORDER BY g",
            _ => $"SELECT l.id,COUNT(t.id) c,CAST(l.id AS TEXT)||'{new string('z', 500)}' pad FROM jl l FULL JOIN jr r ON l.id=r.id FULL JOIN jt t ON r.id=t.id GROUP BY l.id ORDER BY c DESC,l.id",
        };
        if (!cold) { var warm = (DatabaseResultSet)await session.ExecuteAsync(sql); await DrainJoinHeap(warm); await warm.DisposeAsync(); }
        long baseline = LiveHeap.Read(), peak = 0; int joins = 0, groups = 0, sorts = 0;
        var phases = new HashSet<string>();
        var peaks = new Dictionary<string, (long Live, long Charge)>();
        SqlStatementMetrics? active = null; int deferred = 0;
        DatabaseResultSet? delivered = null;
        SqlStatementMetrics.AfterMemoryReservation = (metrics, _) => active = metrics;
        int chunks = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, value) =>
        {
            if (phase == "join-handoff") { joins++; }
            if (phase == "input") { groups++; }
            if (phases.Add(phase) || phase is "join-handoff" or "join-partitioned" or "groups-live" or "batch-live"
                || phase == "join-input" && value % 1000 == 0 || phase == "join-batch-live" && ++chunks % 64 == 0) { Sample(phase); }
        };
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase == "arena") { sorts++; }
            if (phase == "deferred") { deferred++; }
            if (phases.Add("sort-" + phase)) { Sample("sort-" + phase); }
        };
        try
        {
            // Act
            var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
            delivered = result;
            Sample("delivered");
            await DrainJoinHeap(result); Sample("delivery");
            await result.DisposeAsync();
            // Assert
            joins.ShouldBe(shape is "ownership" or "yield" ? 1 : 2);
            if (shape == "yield") { phases.ShouldContain("join-sorter-minimum"); phases.ShouldContain("sort-staged-drained"); }
            else if (shape != "deferred")
            {
                sorts.ShouldBeGreaterThan(0);
                if (shape == "group") { groups.ShouldBeGreaterThan(0); }
                else { phases.ShouldContain("groups-live"); }
            }
            else { deferred.ShouldBeGreaterThan(0); }
            foreach (var phase in peaks)
            { _output.WriteLine($"JOIN_COMBINED_PEAK|shape={shape}|cold={cold}|phase={phase.Key}|live={phase.Value.Live}|charged={phase.Value.Charge}|bound={budget + 262144}"); }
            _output.WriteLine($"JOIN_COMBINED_RESIDUE|shape={shape}|cold={cold}|live={Math.Max(0, LiveHeap.Read() - baseline)}");
            peak.ShouldBeLessThanOrEqualTo(budget + 262144);
            Statements(engine.TempSpace!).ShouldBeEmpty();
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; SqlRowCollector.ObserveSpill = null; SqlStatementMetrics.AfterMemoryReservation = null; }

        void Sample(string phase)
        {
            long live = Math.Max(0, LiveHeap.Read() - baseline); peak = Math.Max(peak, live);
            string category = phase is "delivered" or "delivery" or "sort-reader-publish" ? "delivery"
                : phase.StartsWith("sort-", StringComparison.Ordinal) ? "sort"
                : phase is "join-match-sort-live" or "join-unmatched-live" or "join-ordered" ? "unmatched"
                : phase is "join-batch-live" or "join-partitioned" or "join-index-chunks" ? "probe"
                : phase.StartsWith("join-", StringComparison.Ordinal) ? "build" : "group";
            bool measured = peaks.TryGetValue(category, out var previous);
            long charged = active?.HashMemoryBytes ?? 0;
            if (category == "delivery" && delivered is SqlSpilledResultSet spilled)
            { charged += DeliveredReaderBytes(spilled); }
            if (!measured || live > previous.Live) { peaks[category] = (live, charged); }
            _output.WriteLine($"JOIN_COMBINED|shape={shape}|cold={cold}|phase={phase}|live={live}|charged={charged}|difference={live - charged}");
            (live - charged).ShouldBeLessThanOrEqualTo(262144, customMessage: phase);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_readerBytes")]
    private static extern ref long DeliveredReaderBytes(SqlSpilledResultSet result);

    private static async Task DrainJoinHeap(DatabaseResultSet result)
    {
        await foreach (var row in result.GetRowsAsync())
        { for (int i = 0; i < row.FieldCount; i++) { _ = row.GetValue(i); } }
    }
}
