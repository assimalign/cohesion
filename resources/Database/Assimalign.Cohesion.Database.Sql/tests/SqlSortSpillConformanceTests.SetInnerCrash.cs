using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Set/inner CTAS crash images converge across every durable boundary and sweep disposable stages (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task OpenDatabaseAsync_SetInnerCrashImages_ShouldConverge(bool inner)
    {
        // Arrange: no durable boundary is added by either SELECT operator. Capture
        // all existing CTAS writes/flushes/lengths and the new sealed stage boundary.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var strategy = new CrashCaptureSqlStorageStrategy();
        var stages = new Dictionary<string, CrashCaptureSqlStorageStrategy>();
        byte[]? orphan = null; ulong owner = 0;
        string query = "CREATE TABLE archive AS " + (inner ? "SELECT t.id,t.note FROM o CROSS JOIN t" : "SELECT id,note FROM t UNION ALL SELECT id,note FROM t");
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        await using (var engine = SqlDatabaseEngine.Create("set-inner-crash", new()
        { StorageStrategy = strategy, TempDirectory = Path.Combine(_root, "set-inner-crash"), StatementMemoryBudget = 262144, StatementTempSpaceQuota = 134217728, CheckpointJournalSize = 0 }))
        {
            var database = await engine.CreateDatabaseAsync("db", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)", cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 96).Select(i => $"({i},'{new string('x', 2048)}')")), cancellationToken: guard.Token);
            await session.ExecuteAsync("CREATE TABLE o(id INT)", cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO o VALUES(0),(1)", cancellationToken: guard.Token);
            SqlRowCollector.ObserveSpill = phase =>
            {
                if (phase != (inner ? "join-inner" : "set-operation") || stages.ContainsKey(phase)) { return; }
                stages.Add(phase, strategy.CaptureDurableImages());
                var run = StatementRuns(engine.TempSpace!).First(run => run.IsSealed && run.RecordCount > 0);
                orphan = new byte[RandomAccess.GetLength(Handle(run))]; RandomAccess.Read(Handle(run), orphan, 0);
            };
            SqlPlanExecutor.TableAsStage = (phase, id) =>
            {
                owner = id;
                if (phase == "Copied") { database.Coordinator.Checkpoint(guard.Token); database.CatalogStorage.Checkpoint(); }
            };
            try
            {
                // Act
                using (recorder = strategy.RecordCrashPoints())
                { (await session.ExecuteAsync(query, cancellationToken: guard.Token)).AffectedCount.ShouldBe(192); }
            }
            finally { SqlRowCollector.ObserveSpill = null; SqlPlanExecutor.TableAsStage = null; }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        // Assert: the exact recovered table is absent/reclaimed or complete.
        stages.Count.ShouldBe(1); orphan.ShouldNotBeNull(); int absent = 0, complete = 0;
        var images = stages.Values.Concat(Enumerable.Range(0, recorder.Count).Select(recorder.Open)).ToArray();
        for (int point = 0; point < images.Length; point++)
        {
            string temp = Path.Combine(_root, "set-inner-reopen-" + point); Directory.CreateDirectory(temp);
            await File.WriteAllBytesAsync(Path.Combine(temp, "cohesion-00000000000000000000000000000000.ctmp"), orphan, guard.Token);
            await using var reopened = SqlDatabaseEngine.Create("set-inner-reopen", new() { StorageStrategy = images[point], TempDirectory = temp });
            Directory.GetFiles(temp, "*.ctmp").ShouldBeEmpty();
            var database = await reopened.OpenDatabaseAsync("db", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            if (database.Catalog.TryGetTable("dbo", "archive", out _)) { complete++; }
            else
            {
                absent++; await ReclaimTableAsAsync(database, guard.Token); database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
                (await session.ExecuteAsync(query, cancellationToken: guard.Token)).AffectedCount.ShouldBe(192);
            }
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,note FROM archive ORDER BY id", cancellationToken: guard.Token);
            var rows = await Rows(result, guard.Token); rows.Length.ShouldBe(192);
            for (int i = 0; i < rows.Length; i++) { rows[i].Cells.ShouldBe([i / 2, new string('x', 2048)]); }
            database.IsOffline.ShouldBeFalse(); TempBytes(reopened.TempSpace!).ShouldBe(0); Files(reopened).ShouldBe(0);
        }
        absent.ShouldBeGreaterThan(0); complete.ShouldBeGreaterThan(0);
        _output.WriteLine($"SET_INNER_CRASH|inner={inner}|images={images.Length}|absent={absent}|complete={complete}");
    }
}
