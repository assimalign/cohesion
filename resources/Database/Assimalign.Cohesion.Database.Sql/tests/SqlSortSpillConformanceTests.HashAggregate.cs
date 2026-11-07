using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Hash spill: ordered differential corpus preserves groups, representatives, calls and visits (#1789)")]
    public async Task ExecuteAsync_HashSpillCorpus_ShouldPreserveOrderAndEvaluation()
    {
        // Arrange
        var logs = new[] { new List<long>(), new List<long>() };
        await using var reference = await Create("hash-reference", 67108864, logs[0]);
        await using var spilled = await Create("hash-spill", 262144, logs[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a, 8000); await Seed(b, 8000);
        string[] queries =
        [
            "SELECT id,COUNT(*) FROM t GROUP BY id",
            "SELECT id,COUNT(*),SUM(k),AVG(k),MIN(note),MAX(k) FROM t GROUP BY id",
            "SELECT id/2,COUNT(*),SUM(k) FROM t GROUP BY id/2 HAVING COUNT(*)>0",
            "SELECT id,COUNT(*) FROM t GROUP BY id ORDER BY id DESC",
            "SELECT id,COUNT(*) FROM t GROUP BY id ORDER BY COUNT(*),id DESC LIMIT 2000 OFFSET 7",
            "SELECT stamp(id),COUNT(*) FROM t GROUP BY id",
            "SELECT stamp(id),COUNT(*) FROM t GROUP BY stamp(id)",
            "SELECT id,COUNT(*) FILTER(WHERE stamp(id)%2=0),SUM(stamp(k)) FROM t GROUP BY id",
            "SELECT nextval('s'),id,COUNT(*) FROM t GROUP BY id LIMIT 13 OFFSET 17",
            "SELECT DISTINCT id,note FROM t",
            "SELECT DISTINCT id/2,note FROM t LIMIT 2000 OFFSET 7",
            "SELECT DISTINCT stamp(id),note FROM t",
        ];

        // Act / Assert: compare in order, including spelling of case-insensitive representatives.
        foreach (string sql in queries)
        {
            logs[0].Clear(); logs[1].Clear();
            await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql);
            await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql);
            (await Rows(right)).ShouldBe(await Rows(left), customMessage: sql);
            logs[1].ShouldBe(logs[0], customMessage: sql);
            var am = a.LastStatementMetrics!; var bm = b.LastStatementMetrics!;
            bm.RecordsExamined.ShouldBe(am.RecordsExamined, customMessage: sql);
            bm.JoinCandidatePairs.ShouldBe(am.JoinCandidatePairs);
            bm.HashMemoryBytes.ShouldBe(0); bm.PlainSelectMemoryBytes.ShouldBe(0);
            bm.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(262144);
            var hash = SqlHashSpillMetrics.For(bm).ShouldNotBeNull(customMessage: sql);
            hash.Batches.ShouldBeGreaterThan(0); hash.Passes.ShouldBeGreaterThan(0);
            hash.Runs.ShouldBeGreaterThan(0);
            _output.WriteLine($"HASH|{sql}|bytes={hash.PhysicalBytes}|peak={hash.PeakBytes}|batches={hash.Batches}|passes={hash.Passes}|runs={hash.Runs}|merge={hash.MergePasses}");
        }
        await using var sa = (DatabaseResultSet)await a.ExecuteAsync("SELECT currval('s')");
        await using var sb = (DatabaseResultSet)await b.ExecuteAsync("SELECT currval('s')");
        (await Rows(sb)).ShouldBe(await Rows(sa));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash spill: one existing group never spills; colliding new groups exhaust a coded bound (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HashSkew_ShouldBoundRepartitioning(bool distinct)
    {
        // Arrange
        await using var engine = await Create("hash-skew", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 20000);
        await using (var one = (DatabaseResultSet)await session.ExecuteAsync("SELECT k%1,COUNT(*) FROM t GROUP BY k%1"))
        { (await Rows(one)).Length.ShouldBe(2); SqlHashSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull(); }
        SqlPlanExecutor.HashSpillPartitionHash = _ => 0;
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                distinct ? "SELECT DISTINCT id FROM t" : "SELECT id,COUNT(*) FROM t GROUP BY id"));

            // Assert
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
            error.Message.ShouldContain("8-pass", Case.Sensitive);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
        }
        finally { SqlPlanExecutor.HashSpillPartitionHash = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash spill: unsupported aggregate state retains CS05067 and releases its grant (#1789)")]
    [InlineData("SELECT id,STRING_AGG(note,',') FROM t GROUP BY id")]
    [InlineData("SELECT id,COUNT(DISTINCT k) FROM t GROUP BY id")]
    [InlineData("SELECT id,COUNT(k ORDER BY id) FROM t GROUP BY id")]
    [InlineData("SELECT id,VAR_POP(k) FROM t GROUP BY id")]
    [InlineData("SELECT id,SUM(CAST(k AS NUMERIC)) FROM t GROUP BY id")]
    public async Task ExecuteAsync_UnboundedHashShape_ShouldKeepRefusal(string sql)
    {
        // Arrange
        await using var engine = await Create("hash-exclusion", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));

        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == "CS05067");
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash spill: evaluated partitions release on deterministic cancellation (#1789)")]
    [InlineData("input")]
    [InlineData("batch")]
    [InlineData("partitioned")]
    [InlineData("ordered")]
    public async Task ExecuteAsync_HashCancellation_ShouldReleaseEveryFile(string boundary)
    {
        // Arrange
        await using var engine = await Create("hash-cancel", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000);
        using var cancellation = new CancellationTokenSource();
        bool exercised = false;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        { if (phase == boundary) { exercised = true; cancellation.Cancel(); } };
        try
        {
            // Act / Assert
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "SELECT id,COUNT(*) FROM t GROUP BY id", cancellationToken: cancellation.Token));
            exercised.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }
    }
}
