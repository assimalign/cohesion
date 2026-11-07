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
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Spill deferred keys: drained LINQ tuples are collectible before the arena exists (#1789)")]
    public async Task ExecuteAsync_DeferredKeyHandoff_ShouldReleaseLiveKeys()
    {
        // Arrange
        const long grant = 1048576, constant = 524288;
        var weak = new List<WeakReference>(); var samples = new Dictionary<string, long>();
        bool observe = false, drained = false; long baseline = 0;
        void Sample(string phase) => samples[phase] = Math.Max(samples.GetValueOrDefault(phase), Math.Max(0, LiveHeap.Read() - baseline));
        var builder = SqlDatabaseEngine.CreateBuilder("deferred-key-live");
        builder.Options.RootPath = System.IO.Path.Combine(_root, "deferred-live");
        builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 64 * 1024 * 1024;
        builder.Functions.Add(SqlScalarFunction.Create("observe_key", (long value) =>
        {
            string key = new((char)('a' + value % 23), 1100);
            if (observe && value < 100) { weak.Add(new WeakReference(key)); }
            if (observe && drained && value % 317 == 0) { Sample("input"); }
            return key;
        }));
        builder.Functions.Add(SqlScalarFunction.Create("observe_output", (long value) => value));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 1000);
        const string sql = "SELECT observe_output(id) FROM t ORDER BY observe_key(id)";
        await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
        baseline = LiveHeap.Read(); observe = true;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase is "handoff" or "drained" or "arena") { Sample(phase); }
            if (phase == "drained") { weak.Count.ShouldBe(100); weak.Count(w => w.IsAlive).ShouldBe(0); drained = true; }
        };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
            result.ShouldBeOfType<SqlSpilledResultSet>(); int count = 0;
            await foreach (var row in result.GetRowsAsync()) { if (++count % 317 == 0) { Sample("delivery"); } }

            // Assert
            count.ShouldBe(1000);
            foreach (string phase in new[] { "handoff", "drained", "arena", "input", "delivery" })
            {
                samples.ContainsKey(phase).ShouldBeTrue();
                _output.WriteLine($"LIVE_DEFERRED|{phase}|bytes={samples[phase]}|grant={grant}|constant={constant}");
                samples[phase].ShouldBeLessThanOrEqualTo((long)(grant * 1.1) + constant);
            }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill fitting sort: lending LINQ its buffer preserves warm allocation exactly (#1789)")]
    public async Task SortRows_FittingBufferAdapter_ShouldPreserveAllocation()
    {
        // Arrange: compare the original private sort with the root-enabled private sort.
        await using var engine = await Create("fitting-adapter", 1048576);
        var db = await engine.CreateDatabaseAsync("db");
        var executor = new SqlPlanExecutor(db.DataStorage, db.Catalog, db.IndexManager, db.Definitions);
        var metrics = new SqlStatementMetrics(); ExecutorMetrics(executor) = metrics;
        var rows = Enumerable.Range(0, 100).Select(i => new object?[] { i }).ToList();
        SqlBoundOrdering[] ordering = [new(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Collation.Binary))];
        var evaluator = SqlExpressionEvaluator.ForExecution(null);
        for (int i = 0; i < 10; i++) { Sort(executor, rows, ordering, evaluator, default, false); Sort(executor, rows, ordering, evaluator, default, true); }

        // Act: minimum of three rounds removes incidental runtime allocations.
        long before = Measure(false), after = Measure(true);

        // Assert
        _output.WriteLine($"FITTING_ADAPTER|before={before}|after={after}|rows=100");
        after.ShouldBe(before);
        Sort(executor, rows, ordering, evaluator, default, true).ShouldBe(rows);
        metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);

        long Measure(bool enabled)
        {
            long minimum = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 20; i++) { Sort(executor, rows, ordering, evaluator, default, enabled); }
                minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - start);
            }
            return minimum / 20;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SortRows")]
    private static extern List<object?[]> Sort(SqlPlanExecutor executor, List<object?[]> rows, IReadOnlyList<SqlBoundOrdering> ordering,
        SqlExpressionEvaluator evaluator, CancellationToken token, bool capture);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_metrics")]
    private static extern ref SqlStatementMetrics? ExecutorMetrics(SqlPlanExecutor executor);
}
