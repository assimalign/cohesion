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
    [Fact(DisplayName = "Cohesion Test [Sql] - Window CTAS crash images converge at input, key, output, ordinal and every durable write boundary (#1789)")]
    public async Task OpenDatabaseAsync_WindowCrashImages_ShouldConvergeAndSweep()
    {
        // Arrange: the storage double captures every write/flush/length boundary of
        // existing CTAS publication, plus a database image at each new temp boundary.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var strategy = new CrashCaptureSqlStorageStrategy();
        var stages = new Dictionary<string, CrashCaptureSqlStorageStrategy>();
        byte[]? orphan = null; ulong owner = 0;
        const string query = "CREATE TABLE archive AS SELECT id,note,ROW_NUMBER() OVER(PARTITION BY id%12 ORDER BY id) AS n FROM t";
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        await using (var engine = SqlDatabaseEngine.Create("window-crash", new()
        { StorageStrategy = strategy, TempDirectory = Path.Combine(_root, "window-crash-temp"), StatementMemoryBudget = 262144, StatementTempSpaceQuota = 134217728, CheckpointJournalSize = 0 }))
        {
            var database = await engine.CreateDatabaseAsync("db", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)", cancellationToken: guard.Token);
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0,96).Select(i => $"({i},'{new string('x',2048)}')")), cancellationToken: guard.Token);
            SqlRowCollector.ObserveSpill = phase =>
            {
                if (phase is not ("window-input" or "window-keys" or "window-output" or "window-ordinal") || stages.ContainsKey(phase)) { return; }
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
                { (await session.ExecuteAsync(query, cancellationToken: guard.Token)).AffectedCount.ShouldBe(96); }
            }
            finally { SqlRowCollector.ObserveSpill = null; SqlPlanExecutor.TableAsStage = null; }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        // Assert: temporary pages are disposable, never replayed as database state.
        stages.Keys.Order().ShouldBe(new[] { "window-input", "window-keys", "window-ordinal", "window-output" });
        orphan.ShouldNotBeNull(); int absent = 0, complete = 0;
        var images = stages.Values.Concat(Enumerable.Range(0,recorder.Count).Select(recorder.Open)).ToArray();
        for (int point = 0; point < images.Length; point++)
        {
            string temp = Path.Combine(_root, "window-reopen-" + point); Directory.CreateDirectory(temp);
            await File.WriteAllBytesAsync(Path.Combine(temp, "cohesion-00000000000000000000000000000000.ctmp"), orphan, guard.Token);
            await using var reopened = SqlDatabaseEngine.Create("window-reopen", new() { StorageStrategy = images[point], TempDirectory = temp });
            Directory.GetFiles(temp, "*.ctmp").ShouldBeEmpty();
            var database = await reopened.OpenDatabaseAsync("db", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            if (database.Catalog.TryGetTable("dbo", "archive", out _)) { complete++; }
            else
            {
                absent++; await ReclaimTableAsAsync(database, guard.Token); database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
                (await session.ExecuteAsync(query, cancellationToken: guard.Token)).AffectedCount.ShouldBe(96);
            }
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,note,n FROM archive ORDER BY id", cancellationToken: guard.Token);
            var rows = await Rows(result, guard.Token); rows.Length.ShouldBe(96);
            for (int i = 0; i < rows.Length; i++) { rows[i].Cells.ShouldBe([i,new string('x',2048),(long)(i/12+1)]); }
            await using var source = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,note FROM t ORDER BY id", cancellationToken: guard.Token);
            var original = await Rows(source, guard.Token); original.Length.ShouldBe(96);
            for (int i = 0; i < original.Length; i++) { original[i].Cells.ShouldBe([i,new string('x',2048)]); }
            database.IsOffline.ShouldBeFalse(); TempBytes(reopened.TempSpace!).ShouldBe(0); Files(reopened).ShouldBe(0);
        }
        absent.ShouldBeGreaterThan(0); complete.ShouldBeGreaterThan(0);
        _output.WriteLine($"WINDOW_CRASH|images={images.Length}|absent={absent}|complete={complete}|stages={stages.Count}");
    }
}
