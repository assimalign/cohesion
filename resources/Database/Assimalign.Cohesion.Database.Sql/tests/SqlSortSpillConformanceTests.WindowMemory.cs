using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Window live heap during execution stays bounded and rejects retained partition rows and per-row boxes (#1789)")]
    public async Task ExecuteAsync_WindowLiveHeap_ShouldRejectRetentionMutants()
    {
        // Arrange: three independent baselines per population and implementation.
        // This is a real heap bound, including all decoded objects, not a ledger peak.
        const long grant = 1048576, handoff = 131072, runtimeSlack = 524288;
        var peaks = new long[3, 2];

        foreach (int count in new[] { 16384, 32768 })
        {
            var builder = SqlDatabaseEngine.CreateBuilder("window-live");
            builder.Options.RootPath = Path.Combine(_root, "window-live-" + count);
            builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 536870912;
            builder.Options.BufferPoolCapacity = 1048576;
            await using var engine = await builder.BuildAsync();
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)");
            for (int first = 0; first < count; first += 128)
            { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i},'{new string('x', 2048)}')"))); }
            string sql = $"SELECT id,note,ROW_NUMBER() OVER(PARTITION BY id%{count / 8} ORDER BY id) FROM t";
            await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
            for (int mutant = 0; mutant < 3; mutant++)
            {
                long minimum = long.MaxValue;
                for (int execution = 0; execution < 3; execution++)
                {
                    var retained = new List<object>();
                    WeakReference? sample = null;
                    long baseline = LiveHeap.Read(), peak = 0;
                    int decoded = 0, partitions = 0, samples = 0;
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
                            sample ??= new WeakReference(row);
                            if (mutant == 1) { retained.Add(row); }
                            if (++partitions % 128 == 0) { Sample(); }
                        }
                    };
                    try
                    {
                        // Act: mutants retain objects from the actual decode/partition path.
                        await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
                        // Assert
                        decoded.ShouldBeGreaterThan(count); partitions.ShouldBe(count / 8); samples.ShouldBeGreaterThan(1);
                        minimum = Math.Min(minimum, peak);
                        LiveHeap.Read(); sample.ShouldNotBeNull();
                        if (mutant == 0) { sample.IsAlive.ShouldBeFalse(); }
                        else if (mutant == 1) { sample.IsAlive.ShouldBeTrue(); }
                        GC.KeepAlive(retained);
                    }
                    finally { SqlRowCollector.ObserveBuffer = null; retained.Clear(); }
                    void Sample() { samples++; peak = Math.Max(peak, LiveHeap.Read() - baseline); }
                }
                _output.WriteLine($"WINDOW_HEAP|rows={count}|partitions={count / 8}|mutant={mutant}|peak={minimum}|grant={grant}|handoff={handoff}|runtime_slack={runtimeSlack}");
                void Bound() => minimum.ShouldBeLessThanOrEqualTo(grant + handoff + runtimeSlack);
                peaks[mutant, count == 16384 ? 0 : 1] = minimum;
                if (mutant == 0) { Bound(); }
                else { Should.Throw<ShouldAssertException>(Bound); }
            }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        Growth(0);
        Should.Throw<ShouldAssertException>(() => Growth(1));
        Should.Throw<ShouldAssertException>(() => Growth(2));
        void Growth(int mutant) => (peaks[mutant, 1] - peaks[mutant, 0]).ShouldBeLessThanOrEqualTo(runtimeSlack);
    }
}
