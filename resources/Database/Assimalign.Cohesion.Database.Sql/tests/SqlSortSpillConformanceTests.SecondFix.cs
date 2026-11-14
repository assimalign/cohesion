using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Unwritable known output refuses CS05067 before any temporary write (#1789)")]
    [InlineData(0, 0L)]
    [InlineData(15, 0L)]
    [InlineData(31, 0L)]
    [InlineData(0, 67108864L)]
    [InlineData(15, 67108864L)]
    [InlineData(31, 67108864L)]
    [InlineData(32, 0L)]
    [InlineData(32, 67108864L)]
    [InlineData(31, 0L, 10000, 512)]
    [InlineData(31, 67108864L, 10000, 512)]
    public async Task Add_UnwritableKnownOutput_ShouldRefuseBeforeQuota(int position, long quota, int wideLength = 18000, int ordinaryLength = 2048)
    {
        // Arrange: all 32 rows fit exactly; the following row triggers the handoff.
        var rows = Enumerable.Range(0, 32).Select(i => new object?[] { new string('x', i == position ? wideLength : ordinaryLength) }).ToArray();
        long grant = rows.Sum(SqlSelectMemory.VectorBytes);
        await using var engine = await Create("unwritable-prefix", grant, quota: quota);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics();
        using var collector = new SqlRowCollector([], null, null, null, default, metrics, grant);
        foreach (var row in rows) { collector.Add(row, [], session); }
        int writes = 0;
        BeforeWrite(engine.TempSpace!, () => writes++);
        try
        {
            // Act
            var failure = Should.Throw<DatabaseException>(() => collector.Add([new string('p', position == 32 ? wideLength : ordinaryLength)], [], session));
            _output.WriteLine($"UNWRITABLE_PREFIX|position={position}|width={wideLength}|quota={quota}|code={failure.Diagnostics[0].Code}|writes={writes}");
            // Assert
            failure.Diagnostics[0].Code.ShouldBe("CS05067"); writes.ShouldBe(0);
        }
        finally { BeforeWrite(engine.TempSpace!, null); }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unwritable rows arriving after output drain starts refuse CS05067 before their quota check (#1789)")]
    [InlineData(0L)]
    [InlineData(67108864L)]
    public async Task Add_UnwritableAfterDrainStarts_ShouldRefuseBeforeQuota(long quota)
    {
        // Arrange: an empty prefix permits the drain boundary even with no temp quota.
        await using var engine = await Create("unwritable-append", 1048576, quota: quota);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var rows = new List<object?[]>();
        SqlSelectMemory memory = default;
        var metrics = new SqlStatementMetrics();
        bool draining = false;
        SqlOutputSpool? output = null;
        object?[] arriving = [null];
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase == "output-drained") { draining = true; arriving[0] = new string('x', 18000); }
        };
        try
        {
            if (quota > 0) { output = SqlOutputSpool.Add(rows, ["first"], ref memory, metrics, 1048576, session, default); }
            // Act
            var failure = Should.Throw<DatabaseException>(() =>
            {
                if (output is not null) { output.Append(arriving, default); }
                else { SqlOutputSpool.Add(rows, arriving, ref memory, metrics, 1048576, session, default); }
            });
            _output.WriteLine($"UNWRITABLE_APPEND|quota={quota}|draining={draining}|code={failure.Diagnostics[0].Code}");
            // Assert
            draining.ShouldBeTrue(); failure.Diagnostics[0].Code.ShouldBe("CS05067");
        }
        finally { SqlRowCollector.ObserveSpill = null; output?.Dispose(); }
        metrics.HashMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unwritable join output preserves the memory refusal ahead of a later WHERE error (#1789)")]
    [InlineData(0L)]
    [InlineData(67108864L)]
    public async Task ExecuteAsync_UnwritableJoinOutput_ShouldPreserveFirstRefusal(long quota)
    {
        // Arrange: the wide source reaches its memory boundary before fails_on(317).
        await using var engine = await Create("unwritable-join", 262144, quota: quota);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT d.id,d.note FROM (SELECT id,RPAD(note,CASE WHEN id=1 THEN 18000 ELSE 1024 END,'x') AS note FROM t) d CROSS JOIN u WHERE fails_on(d.id)>=0"));
        _output.WriteLine($"UNWRITABLE_JOIN|quota={quota}|code={failure.Diagnostics[0].Code}");
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05067");
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Excluded CTAS sources retain copy admission without spill-root credit (#1789)")]
    [InlineData("recursive")]
    [InlineData("json")]
    [InlineData("domain")]
    public async Task ExecuteAsync_ExcludedCopyCredit_ShouldKeepAdmission(string shape)
    {
        // Arrange: sources fit, but a full copy buffer cannot fit alongside them.
        // Wide recursive rows leave room for the recursive reader's own reservations.
        bool recursive = shape == "recursive";
        int count = recursive ? 210 : 1000;
        await using var engine = await Create("excluded-credit", recursive ? 524288 : 65000);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        if (shape == "domain")
        { await session.ExecuteAsync("CREATE DOMAIN guarded AS TEXT CHECK(JSON_VALUE('[1]', '$[0]' RETURNING BIGINT)=1)"); }
        if (!recursive)
        {
            await session.ExecuteAsync("CREATE TABLE t(note " + (shape == "domain" ? "guarded" : "TEXT") + ")");
            for (int first = 0; first < count; first += 200)
            { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Repeat("(NULL)", Math.Min(200, count - first)))); }
        }
        string source = shape switch
        {
            "recursive" => $"WITH RECURSIVE r(n) AS (SELECT 0 UNION ALL SELECT n+1 FROM r WHERE n<{count - 1}) SELECT RPAD(CAST(n AS TEXT),1024,'x') AS note FROM r",
            "json" => "SELECT JSON_VALUE('null', '$' RETURNING VARCHAR(10)) AS note FROM t",
            _ => "SELECT note FROM t",
        };
        long admitted = 0; int encoded = 0;
        SqlPlanExecutor.TableAsSourceAdmitted = bytes => admitted = bytes;
        SqlPlanExecutor.TableAsRecordEncoded = _ => encoded++;
        Exception? exception;
        try
        {
            // Act
            exception = await Record.ExceptionAsync(async () => await session.ExecuteAsync("CREATE TABLE archive AS " + source));
        }
        finally { SqlPlanExecutor.TableAsSourceAdmitted = null; SqlPlanExecutor.TableAsRecordEncoded = null; }
        string code = exception is DatabaseException failure ? failure.Diagnostics[0].Code! : exception is null ? "ok" : exception.GetType().Name;
        _output.WriteLine($"EXCLUDED_CREDIT|shape={shape}|code={code}|admitted={admitted}|encoded={encoded}");
        // Assert
        admitted.ShouldBeGreaterThan(0);
        if (recursive) { encoded.ShouldBeGreaterThan(0); }
        code.ShouldBe("CS05067"); database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }
}
