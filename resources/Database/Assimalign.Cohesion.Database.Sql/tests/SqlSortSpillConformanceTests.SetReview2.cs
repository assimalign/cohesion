using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Final set output records its refusal before its cache is read (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SetOutputCacheDamage_ShouldKeepArchivedRefusal(bool ordered)
    {
        // Arrange: both converted inputs fit; only final output or ordering spools.
        await using var engine = await Create("set-output-cache-review", 262144, quota: 134217728);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, ordered ? 480 : 900);
        bool output = false, injected = false; int stageAppends = 0;
        SqlRowCollector.ObserveBuffer = (phase, _) => { if (phase == "set-append") { stageAppends++; } };
        SqlRowCollector.ObserveSpill = phase => { if (phase is "output-spool" or "complete") { output = true; } };
        BeforeRead(engine.TempSpace!, () =>
        {
            if (!output || injected || CachedRelations(null)?.Any() != true) { return; }
            var cache = StatementRuns(engine.TempSpace!).Where(run => run.IsSealed && run.RecordCount > 0)
                .OrderByDescending(run => run.RecordCount).First();
            for (long offset = 0; offset < RandomAccess.GetLength(Handle(cache)); offset += 8192)
            { RandomAccess.Write(Handle(cache), new byte[] { 255 }, offset); }
            injected = true;
        });
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                    "WITH c AS (SELECT id FROM t UNION ALL SELECT id FROM t" + (ordered ? " ORDER BY id,id,id,id,id,id,id,id" : "") + ") SELECT id FROM c");
                await Rows(result);
            });
            // Assert
            _output.WriteLine($"SET_OUTPUT_CACHE_REVIEW|ordered={ordered}|code={failure.Diagnostics[0].Code}|injected={injected}|stageAppends={stageAppends}");
            failure.Diagnostics[0].Code.ShouldBe("CS05067");
            stageAppends.ShouldBe(0); output.ShouldBeTrue(); injected.ShouldBeTrue();
            WindowFailureData(failure, nameof(SqlOperatorSpillRefusal)).ShouldBeTrue(); CacheDamageCause(failure).ShouldBeTrue();
            WindowFailureData(failure, "SqlExistingRelationTemp").ShouldBeTrue();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { BeforeRead(engine.TempSpace!, null); SqlRowCollector.ObserveBuffer = null; SqlRowCollector.ObserveSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Cache damage after inner or set-body admission retains the archived refusal (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_NewHandoffCacheDamage_ShouldKeepArchivedRefusal(bool setBody)
    {
        // Arrange
        await using var engine = await Create("cache-handoff-review", 262144, quota: 134217728);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        bool injected = false, innerStarted = false, setStarted = false;
        SqlRowCollector.ObserveBuffer = (phase, _) =>
        {
            if (phase == "inner-append") { innerStarted = true; }
            if (phase == "set-append") { setStarted = true; }
        };
        BeforeRead(engine.TempSpace!, () =>
        {
            if (injected || !innerStarted) { return; }
            var cache = StatementRuns(engine.TempSpace!).Where(run => run.IsSealed && run.RecordCount > 0)
                .OrderByDescending(run => run.RecordCount).First();
            for (long offset = 0; offset < RandomAccess.GetLength(Handle(cache)); offset += 8192)
            { RandomAccess.Write(Handle(cache), new byte[] { 255 }, offset); }
            injected = true;
        });
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(setBody
                    ? "WITH c AS (SELECT id FROM t UNION ALL SELECT id FROM t) SELECT c.id,u.id FROM u CROSS JOIN c"
                    : "WITH c AS (SELECT id,note FROM t) SELECT u.id,c.id FROM u CROSS JOIN c");
                await Rows(result);
            });
            // Assert: the archived binary refuses before either damage hook can run.
            _output.WriteLine($"NEW_CACHE_REVIEW|setBody={setBody}|code={failure.Diagnostics[0].Code}|injected={injected}|inner={innerStarted}|set={setStarted}");
            failure.Diagnostics[0].Code.ShouldBe("CS05067");
            injected.ShouldBeTrue(); CacheDamageCause(failure).ShouldBeTrue();
            WindowFailureData(failure, nameof(SqlOperatorSpillRefusal)).ShouldBeTrue();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { BeforeRead(engine.TempSpace!, null); SqlRowCollector.ObserveBuffer = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window admission keeps the statement gate after committed DDL (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_WindowGateFlip_ShouldKeepStatementDecision(bool afterPlan)
    {
        // Arrange
        await using var engine = await Create("window-gate-review", 262144, quota: 134217728);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        await using var ddl = await database.CreateSessionAsync();
        await ddl.ExecuteAsync("CREATE FUNCTION gate_marker(s TEXT) RETURNS TEXT IMMUTABLE RETURN s");
        bool flipped = false; string code = "ok"; int count = 0;
        void Flip()
        {
            if (flipped) { return; }
            flipped = true;
            ddl.ExecuteAsync("DROP FUNCTION gate_marker(TEXT)").GetAwaiter().GetResult();
        }
        SqlRowCollector.ObserveSpill = phase => { if (!afterPlan && phase == "output-spool") { Flip(); } };
        SqlQueryExecutor.AfterPlan = (_, _) => { if (afterPlan) { Flip(); } };
        try
        {
            // Act
            try
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                    "WITH earlier AS (SELECT id,note FROM t), later AS (SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY id) AS rn FROM t) SELECT later.id,later.rn FROM earlier JOIN later ON earlier.id=later.id");
                count = (await Rows(result)).Length;
            }
            catch (DatabaseException failure) { code = failure.Diagnostics[0].Code!; }
            // Assert: archived base completes with 4,000 rows; statement capture safely refuses.
            _output.WriteLine($"WINDOW_GATE_REVIEW|afterPlan={afterPlan}|code={code}|rows={count}|flipped={flipped}");
            flipped.ShouldBeTrue(); code.ShouldBe("CS05079");
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; SqlQueryExecutor.AfterPlan = null; }
    }

    private static bool CacheDamageCause(Exception failure)
    {
        if (failure is DatabaseException database && database.Diagnostics.Any(diagnostic => diagnostic.Code == "CK05004")) { return true; }
        if (failure is SqlExecutionException sql && sql.Diagnostics.Any(diagnostic => diagnostic.Code == "CK05004")) { return true; }
        if (failure.InnerException is { } inner && CacheDamageCause(inner)) { return true; }
        foreach (object? value in failure.Data.Values)
        { if (value is Exception cause && CacheDamageCause(cause)) { return true; } }
        return false;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_relations")]
    private static extern ref ConditionalWeakTable<List<object?[]>, SqlSpilledResultSet>? CachedRelations(SqlSpilledResultSet? declaringType);
}
