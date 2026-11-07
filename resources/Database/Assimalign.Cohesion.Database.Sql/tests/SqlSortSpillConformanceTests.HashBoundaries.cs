using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Hash ordinal merge: source reader workspace narrows the record bound with CS05067 (#1789)")]
    public async Task ExecuteAsync_HashOrdinalRecord_ShouldRefuseBeforeKernelBound()
    {
        // Arrange: the 5,800-byte key fits the handoff record ceiling, but the
        // ordinal sort's grant must also leave room for its active input reader.
        await using var engine = await Create("hash-record-bound", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 100);
        await session.ExecuteAsync("UPDATE t SET note=@note", new System.Collections.Generic.Dictionary<string, object?> { ["note"] = new string('x', 5800) });

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id,COUNT(*) FROM t GROUP BY id,note"));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash cleanup: close failures preserve the initiating projection error and release the shared owner (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HashCloseFailure_ShouldPreserveInitiatingError(bool projectionFails)
    {
        // Arrange
        await using var engine = await Create("hash-close", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000); await session.ExecuteAsync("BEGIN");
        bool injected = false;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        {
            if (phase != "ordered") { return; }
            var run = System.Linq.Enumerable.First(StatementRuns(engine.TempSpace!));
            RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle); injected = true;
        };
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                projectionFails ? "SELECT fails_on(id-4000),COUNT(*) FROM t GROUP BY id" : "SELECT id,COUNT(*) FROM t GROUP BY id"));

            // Assert
            injected.ShouldBeTrue(); error.Diagnostics[0].Code.ShouldBe(projectionFails ? "CS05007" : "CK05005");
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
            await session.ExecuteAsync("COMMIT"); engine.OfflineDatabases.ShouldBeEmpty();
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash admission: exact fitting charges and refusal peaks remain pinned without temp space (#1789)")]
    [InlineData(false, 744, 262144, 261888, true)]
    [InlineData(false, 745, 262144, 261984, false)]
    [InlineData(true, 1170, 262144, 262080, true)]
    [InlineData(true, 1171, 262144, 262080, false)]
    [InlineData(true, 1171, 262208, 262208, false)]
    public async Task ExecuteAsync_HashBoundary_ShouldKeepExactChargedBytes(bool distinct, int rows, long grant, long peak, bool succeeds)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("hash-boundary"); builder.Options.StatementMemoryBudget = grant;
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, rows);
        string sql = distinct ? "SELECT DISTINCT id FROM t LIMIT 0" : "SELECT id,COUNT(*) FROM t GROUP BY id LIMIT 0";

        // Act
        if (succeeds) { await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql); (await Rows(result)).ShouldBeEmpty(); }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        }

        // Assert
        var metrics = session.LastStatementMetrics!;
        metrics.PeakHashMemoryBytes.ShouldBe(peak); metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
        SqlHashSpillMetrics.For(metrics).ShouldBeNull(); engine.TempSpace.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash failures preserve volatile input boundaries, first errors and access work (#1789)")]
    [InlineData("SELECT id,COUNT(*) FILTER(WHERE fails_on(id-4000)=0) FROM t GROUP BY id")]
    [InlineData("SELECT fails_on(id-4000),COUNT(*) FROM t GROUP BY fails_on(id-4000)")]
    [InlineData("SELECT id,SUM(fails_on(id-4000)) FROM t GROUP BY id")]
    [InlineData("SELECT id,COUNT(*) FROM t WHERE fails_on(id-4000)>=0 GROUP BY stamp(id)")]
    public async Task ExecuteAsync_HashFirstError_ShouldMatchCallsAndVisits(string sql)
    {
        // Arrange
        var logs = new[] { new System.Collections.Generic.List<long>(), new System.Collections.Generic.List<long>() };
        await using var aEngine = await Create("hash-error-a", 67108864, logs[0]);
        await using var bEngine = await Create("hash-error-b", 262144, logs[1]);
        await using var a = await (await aEngine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await bEngine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a, 8000); await Seed(b, 8000);

        // Act
        var left = await Should.ThrowAsync<DatabaseException>(async () => await a.ExecuteAsync(sql));
        var right = await Should.ThrowAsync<DatabaseException>(async () => await b.ExecuteAsync(sql));

        // Assert
        right.Diagnostics[0].Code.ShouldBe(left.Diagnostics[0].Code); right.Message.ShouldBe(left.Message);
        logs[1].ShouldBe(logs[0]); b.LastStatementMetrics!.RecordsExamined.ShouldBe(a.LastStatementMetrics!.RecordsExamined);
        b.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); b.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        TempBytes(bEngine.TempSpace!).ShouldBe(0); Files(bEngine).ShouldBe(0);
    }
}
