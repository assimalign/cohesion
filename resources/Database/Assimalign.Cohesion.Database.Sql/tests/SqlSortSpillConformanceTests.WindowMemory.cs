using System;
using System.Buffers;
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
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapAcceptance;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Window input membership caches remain charged throughout every spilled stage (#1789)")]
    public async Task ExecuteAsync_WindowCachedInputLiveHeap_ShouldCountPendingVectors()
    {
        // Arrange: the outer scalar owns the frame shared with the derived body's IN vector.
        const long grant = 4194304, floor = grant - 131072;
        using var pool = new HashPoolEvents();
        using var pooledImages = new WindowPoolLease();
        foreach (int count in new[] { 32768, 65536 })
        {
            await using var engine = await CreateWindowAsync("window-cache-live-" + count, grant, quota: 536870912);
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await Seed(session, count / 256);
            await session.ExecuteAsync("INSERT INTO u VALUES" + string.Join(',', Enumerable.Range(2, 255).Select(i => $"({i})")));
            await session.ExecuteAsync("CREATE TABLE big(k INT)");
            for (int first = 0; first < 8192; first += 128)
            { await session.ExecuteAsync("INSERT INTO big VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i%73})"))); }
            const string sql = "SELECT d.id,ROW_NUMBER() OVER(PARTITION BY d.id%100 ORDER BY d.id) FROM (SELECT id FROM t WHERE k IN (SELECT k FROM big)) d CROSS JOIN u WHERE d.id>(SELECT MIN(id) FROM t)";
            if (Environment.GetEnvironmentVariable("SQ_L4B_RECORD") is { } reference)
            {
                string code = "ok";
                try { await WarmWindowHeapAsync(session, sql); }
                catch (DatabaseException failure) { code = failure.Diagnostics[0].Code!; }
                Directory.CreateDirectory(reference);
                await File.WriteAllTextAsync(Path.Combine(reference, $"cache-live-{count}.txt"), code);
                _output.WriteLine($"WINDOW_CACHE_REFERENCE|rows={count}|G={grant}|code={code}");
                continue;
            }
            await WarmWindowHeapAsync(session, sql);
            var batch = new Batch(pool, _output, nameof(ExecuteAsync_WindowCachedInputLiveHeap_ShouldCountPendingVectors));
            var sampled = await batch.MeasureAsync($"rows={count}", async readHeap =>
            {
                pooledImages.Drain(pool);
                long before = readHeap(), peak = before, pending = -1;
                int decoded = 0, resets = 0, drains = 0;
                SqlRowCollector.ObserveBuffer = (phase, value) =>
                {
                    if (phase == "window-reset") { resets++; pending = ((SqlSubqueryValues)value).PendingStatementMemoryBytes; }
                    if (phase == "window-drain" && drains++ == 0 || phase == "window-decoded" && ++decoded % 4096 == 0)
                    { peak = Math.Max(peak, readHeap()); }
                };
                try
                {
                    // Act
                    await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
                    long after = readHeap(), measured = peak - Math.Min(before, after);
                    _output.WriteLine($"WINDOW_CACHE_HEAP|rows={count}|peak={measured}|floor={floor}|before={before}|after={after}|pending={pending}|resets={resets}|drains={drains}");
                    // Assert: the floor includes the cache plus the sort's remaining workspace.
                    resets.ShouldBe(1); drains.ShouldBeGreaterThan(0); pending.ShouldBe(0);
                    session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
                    TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
                    return measured;
                }
                finally { SqlRowCollector.ObserveBuffer = null; }
            });
            await batch.CheckAsync(() => [Floor("window cached workspace", sampled.Value, floor, sampled.Term),
                Ceiling("window cached peak", sampled.Value, grant + 131072 + 65536, sampled.Term)], () =>
            {
                long measured = sampled.Value;
                measured.ShouldBeGreaterThanOrEqualTo(floor);
                measured.ShouldBeLessThanOrEqualTo(grant + 131072 + 65536);
            });
            await batch.CompleteAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window live heap during execution stays bounded and rejects retained partition rows and per-row boxes (#1789)")]
    public async Task ExecuteAsync_WindowLiveHeap_ShouldRejectRetentionMutants()
    {
        // Arrange: every execution uses the lower quiescent reading before and after it.
        // This is a real heap bound, including all decoded objects, not a ledger peak.
        const long grant = 1048576, handoff = 131072, runtimeSlack = 65536, floor = grant - 131072;
        var peaks = new long[4, 2];
        var readings = new Sample<long>[4, 2];
        var builder = SqlDatabaseEngine.CreateBuilder("window-live");
        builder.Options.RootPath = Path.Combine(_root, "window-live");
        builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 536870912;
        builder.Options.BufferPoolCapacity = 1048576;
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)");
        for (int first = 0; first < 32768; first += 128)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i},'{new string('x', 2048)}')"))); }
        using var pool = new HashPoolEvents();
        using var pooledImages = new WindowPoolLease();
        await WarmWindowHeapAsync(session, "SELECT id,note,ROW_NUMBER() OVER(PARTITION BY id%2048 ORDER BY id) FROM t WHERE id<16384");
        pooledImages.Drain(pool);
        var acceptance = new Batch(pool, _output, nameof(ExecuteAsync_WindowLiveHeap_ShouldRejectRetentionMutants));
        foreach (int count in new[] { 16384, 32768 })
        {
            string sql = $"SELECT id,note,ROW_NUMBER() OVER(PARTITION BY id%{count / 8} ORDER BY id) FROM t WHERE id<{count}";
            await WarmWindowHeapAsync(session, sql);
            for (int mutantIndex = 0; mutantIndex < 4; mutantIndex++)
            {
                int mutant = mutantIndex;
                long minimum = long.MaxValue;
                for (int executionIndex = 0; executionIndex < 1; executionIndex++)
                {
                    int execution = executionIndex;
                    var sampled = await acceptance.MeasureAsync($"rows={count}|mutant={mutant}|execution={execution}", async readHeap =>
                    {
                        pooledImages.Drain(pool);
                        var retained = new List<object>();
                        WeakReference? sample = null;
                        long before = readHeap(), peak = before, trimmed = pool.Trimmed;
                        int decoded = 0, partitions = 0, samples = 0, drains = 0, weakChecks = 0;
                        SqlRowCollector.ObserveBuffer = (phase, value) =>
                        {
                            if (phase == "window-decoded")
                            {
                                var row = (object?[])value;
                                if (mutant == 2) { retained.Add(row[0]!); }
                                if (++decoded % 4096 == 0) { Sample(); }
                            }
                            if (phase == "window-partition")
                            {
                                var row = ((List<object?[]>)value)[0];
                                if (sample is not null && partitions == 1)
                                {
                                    readHeap(); weakChecks++;
                                    if (mutant == 1) { sample.IsAlive.ShouldBeTrue(); }
                                    else { sample.IsAlive.ShouldBeFalse(); }
                                }
                                if (partitions == 0) { sample = new WeakReference(row); }
                                if (mutant == 1) { retained.Add(row); }
                                if (mutant == 3)
                                { foreach (var inputRow in (List<object?[]>)value) { retained.Add(inputRow[0]!); } }
                                if (++partitions % 128 == 0) { Sample(); }
                            }
                            if (phase == "window-drain" && drains++ == 0) { Sample(); }
                        };
                        try
                        {
                            // Act: mutants retain objects from the actual decode/partition path.
                            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
                            // Assert
                            decoded.ShouldBeGreaterThan(count); partitions.ShouldBe(count / 8); samples.ShouldBeGreaterThan(1);
                            weakChecks.ShouldBe(1); drains.ShouldBeGreaterThan(0);
                            if (mutant == 3) { retained.Count.ShouldBe(count); }
                            GC.KeepAlive(retained);
                            retained.Clear();
                            long after = readHeap();
                            long measured = peak - Math.Min(before, after);
                            _output.WriteLine($"WINDOW_HEAP_SAMPLE|rows={count}|mutant={mutant}|execution={execution}|peak={measured}|floor={floor}|before={before}|after={after}|pool_trimmed={pool.Trimmed-trimmed}|drains={drains}");
                            return measured;
                        }
                        finally
                        {
                            SqlRowCollector.ObserveBuffer = null; retained.Clear();
                        }
                        void Sample() { samples++; peak = Math.Max(peak, readHeap()); }
                    });
                    readings[mutant, count == 16384 ? 0 : 1] = sampled;
                    await acceptance.CheckAsync(() => [Floor("window live workspace", sampled.Value, floor, sampled.Term)],
                        () => sampled.Value.ShouldBeGreaterThanOrEqualTo(floor));
                    minimum = Math.Min(minimum, sampled.Value);
                }
                var current = readings[mutant, count == 16384 ? 0 : 1];
                await acceptance.CheckAsync(() => mutant == 0
                    ? [Ceiling("window peak", current.Value, grant + handoff + runtimeSlack, current.Term)]
                    : [Floor("window retaining mutant", current.Value, grant + handoff + runtimeSlack, current.Term, true)], () =>
                {
                    minimum = current.Value;
                    _output.WriteLine($"WINDOW_HEAP|rows={count}|partitions={count / 8}|mutant={mutant}|peak={minimum}|grant={grant}|handoff={handoff}|runtime_slack={runtimeSlack}");
                    void Bound() => minimum.ShouldBeLessThanOrEqualTo(grant + handoff + runtimeSlack);
                    peaks[mutant, count == 16384 ? 0 : 1] = minimum;
                    if (mutant == 0)
                    {
                        Bound();
                    }
                    else
                    {
                        Should.Throw<ShouldAssertException>(Bound);
                    }
                });
            }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        await acceptance.CheckAsync(() => Enumerable.Range(0, 4).Select(mutant => mutant == 0
            ? Ceiling("window population growth", readings[mutant, 1].Value - readings[mutant, 0].Value, 131072,
                readings[mutant, 1].Term + readings[mutant, 0].Term)
            : Floor("window mutant population growth", readings[mutant, 1].Value - readings[mutant, 0].Value, 131072,
                readings[mutant, 1].Term + readings[mutant, 0].Term, true)).ToArray(), () =>
        {
            for (int mutant = 0; mutant < 4; mutant++)
            {
                for (int population = 0; population < 2; population++)
                {
                    peaks[mutant, population] = readings[mutant, population].Value;
                }
            }

            Growth(0);
            Should.Throw<ShouldAssertException>(() => Growth(1));
            Should.Throw<ShouldAssertException>(() => Growth(2));
            Should.Throw<ShouldAssertException>(() => Growth(3));
        });
        await acceptance.CompleteAsync();
        void Growth(int mutant) => (peaks[mutant, 1] - peaks[mutant, 0]).ShouldBeLessThanOrEqualTo(131072);
    }

    private static async Task WarmWindowHeapAsync(SqlDatabaseSession session, string sql)
    { await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql); }
}
