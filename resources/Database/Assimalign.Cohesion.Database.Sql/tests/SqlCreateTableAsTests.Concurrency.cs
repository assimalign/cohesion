using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCreateTableAsTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS: source writes after reservation do not change its statement snapshot")]
    public async Task ExecuteAsync_SourceChangesDuringBuild_ShouldUseOneSnapshot()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var reserved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("ctas-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var copying = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(writer, timeout.Token);
        SqlPlanExecutor.TableAsStage = (stage, _) => { if (stage == "Reserved") { reserved.Set(); release.Wait(timeout.Token); } };
        var build = DedicatedTestTask.Run(() => ExecAsync(copying, "CREATE TABLE copy AS SELECT id FROM src", timeout.Token), timeout.Token);
        try
        {
            reserved.Wait(timeout.Token);

            // Act
            await ExecAsync(writer, "UPDATE src SET id=100 WHERE id=1", timeout.Token);
            await ExecAsync(writer, "INSERT INTO src VALUES(66)", timeout.Token);
            release.Set();
            await build;

            // Assert
            (await RowsAsync(writer, "SELECT id FROM copy ORDER BY id", timeout.Token)).Select(r => (int)r[0]!).ShouldBe(Enumerable.Range(1, 65));
        }
        finally
        {
            SqlPlanExecutor.TableAsStage = null;
            release.Set();
            try { await build; } catch (Exception) { /* Observe the actor without replacing the test failure. */ }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS: publication readers and reclamation exercise hidden and complete owners")]
    public async Task ExecuteAsync_ConcurrentPublicationAndReclamation_ShouldNeverExposePartialRows()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var copying = new ManualResetEventSlim();
        using var examined = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("ctas-race", new() { MaintenanceInterval = TimeSpan.FromMilliseconds(2) });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(writer, timeout.Token);
        int current = 0, hidden = 0, complete = 0, passes = 0, builds = 0;
        SqlPlanExecutor.TableAsStage = (stage, owner) =>
        {
            if (stage != "Copied") { return; }
            // Force the first race, then continue until every progress counter moves.
            if (Volatile.Read(ref current) == 1)
            {
                copying.Set();
                examined.Wait(timeout.Token);
                database.DataStorage.HasOwnerPages(owner).ShouldBeTrue();
            }
            Thread.Yield();
        };
        var reading = DedicatedTestTask.Run(async () =>
        {
            copying.Wait(timeout.Token);
            while (!stop.IsCancellationRequested)
            {
                int name = Volatile.Read(ref current);
                if (!database.Catalog.TryGetTable("dbo", "copy" + name, out _))
                {
                    Interlocked.Increment(ref hidden);
                    database.TableReclamation.Request();
                    database.TableReclamation.RunBatch(timeout.Token);
                    Interlocked.Increment(ref passes);
                    examined.Set();
                }
                else
                {
                    var rows = await RowsAsync(reader, "SELECT id FROM copy" + name + " ORDER BY id", timeout.Token);
                    rows.Select(r => (int)r[0]!).ShouldBe(Enumerable.Range(1, 65));
                    Interlocked.Increment(ref complete);
                }
                await Task.Delay(1, timeout.Token);
            }
        }, timeout.Token);
        try
        {
            // Act
            var elapsed = Stopwatch.StartNew();
            do
            {
                int name = Interlocked.Increment(ref current);
                await ExecAsync(writer, "CREATE TABLE copy" + name + " AS SELECT id FROM src", timeout.Token);
                builds++;
                // Give the independent reader an opportunity to observe this publication.
                await Task.Delay(1, timeout.Token);
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || Volatile.Read(ref hidden) == 0 ||
                Volatile.Read(ref complete) == 0 || Volatile.Read(ref passes) == 0 || builds < 2);
        }
        finally
        {
            SqlPlanExecutor.TableAsStage = null;
            examined.Set();
            stop.Cancel();
            await reading;
        }

        // Assert
        hidden.ShouldBeGreaterThan(0);
        complete.ShouldBeGreaterThan(0);
        passes.ShouldBeGreaterThan(0);
        builds.ShouldBeGreaterThan(1);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        await ReclaimAsync(database, timeout.Token);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        _output.WriteLine($"CTAS builds={builds}; hidden observations={hidden}; complete reads={complete}; concurrent reclamation passes={passes}");
    }
}
