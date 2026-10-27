using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCreateTableAsTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS: declared script receipt, schema ownership and completed rows survive reopen")]
    public async Task Build_DeclaredTableAsScript_ShouldPublishReceiptAndSkipReplay()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var strategy = new CrashCaptureSqlStorageStrategy();
        const string script = "CREATE TABLE copy (n) AS SELECT 1 WITH DATA; INSERT INTO copy VALUES(2);";
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-script");
        builder.Options.StorageStrategy = strategy;
        builder.AddDatabase("db", database => database.Script("copy-script", script));
        await using (var engine = await builder.BuildAsync(timeout.Token))
        {
            var database = await engine.OpenDatabaseAsync("db", timeout.Token);
            database.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(2);
            database.Catalog.Tables.ShouldHaveSingleItem().Owner.ShouldBe(DatabaseObjectOwner.Schema);
        }

        // Act
        var reopened = SqlDatabaseEngine.CreateBuilder("ctas-script-reopen");
        reopened.Options.StorageStrategy = strategy.CaptureDurableImages();
        reopened.AddDatabase("db", database => database.Script("copy-script", script));
        await using var recovered = await reopened.BuildAsync(timeout.Token);
        var copy = await recovered.OpenDatabaseAsync("db", timeout.Token);
        await using var session = await copy.CreateSessionAsync(timeout.Token);

        // Assert
        (await RowsAsync(session, "SELECT n FROM copy ORDER BY n", timeout.Token)).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        copy.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS: every durable crash boundary recovers to absent or complete and reclaims abandoned builds")]
    [InlineData(StorageCommitDurability.Synchronous, false)]
    [InlineData(StorageCommitDurability.Grouped, false)]
    [InlineData(StorageCommitDurability.None, false)]
    [InlineData(StorageCommitDurability.Synchronous, true)]
    [InlineData(StorageCommitDurability.Grouped, true)]
    [InlineData(StorageCommitDurability.None, true)]
    public async Task Reopen_TableAsCrashBoundaries_ShouldRecoverAbsentOrComplete(StorageCommitDurability durability, bool cancel)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var strategy = new CrashCaptureSqlStorageStrategy(durability);
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        ulong prepared = 0;
        int checkpoints = 0;
        await using (var engine = SqlDatabaseEngine.Create("ctas-crash", new() { StorageStrategy = strategy, Durability = durability }))
        {
            var database = await engine.CreateDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await SeedAsync(session, timeout.Token);
            SqlPlanExecutor.TableAsStage = (stage, owner) =>
            {
                prepared = owner;
                if (stage != "Copied") { return; }
                database.Coordinator.Checkpoint(timeout.Token);
                database.CatalogStorage.Checkpoint();
                checkpoints++;
                if (cancel) { cancellation.Cancel(); }
            };
            try
            {
                // Act
                using (recorder = strategy.RecordCrashPoints())
                {
                    if (cancel) { await Should.ThrowAsync<OperationCanceledException>(() => ExecAsync(session, "CREATE TABLE copy AS SELECT id FROM src", cancellation.Token)); }
                    else { await ExecAsync(session, "CREATE TABLE copy AS SELECT id FROM src", timeout.Token); }
                    await ReclaimAsync(database, timeout.Token);
                    database.Coordinator.Checkpoint(timeout.Token);
                    database.CatalogStorage.Checkpoint();
                }
            }
            finally { SqlPlanExecutor.TableAsStage = null; }
        }

        // Assert
        checkpoints.ShouldBe(cancel ? 1 : 2);
        recorder.Count.ShouldBeGreaterThan(1);
        int absent = 0, complete = 0;
        for (int point = 0; point < recorder.Count; point++)
        {
            await using var recovered = SqlDatabaseEngine.Create("ctas-recover", new() { StorageStrategy = recorder.Open(point), Durability = durability });
            var database = await recovered.OpenDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            if (database.Catalog.TryGetTable("dbo", "copy", out var table))
            {
                complete++;
                table.ObjectId.ShouldBe(prepared);
                (await RowsAsync(session, "SELECT * FROM copy ORDER BY id", timeout.Token)).Select(r => (int)r[0]!).ShouldBe(Enumerable.Range(1, 65));
                await ReclaimAsync(database, timeout.Token);
                database.DataStorage.HasOwnerPages(prepared).ShouldBeTrue();
            }
            else
            {
                absent++;
                await ReclaimAsync(database, timeout.Token);
                database.DataStorage.HasOwnerPages(prepared).ShouldBeFalse();
                await ExecAsync(session, "CREATE TABLE copy AS SELECT id FROM src", timeout.Token);
            }
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            (await RowsAsync(session, "SELECT * FROM copy ORDER BY id", timeout.Token)).Select(r => (int)r[0]!).ShouldBe(Enumerable.Range(1, 65));
        }
        absent.ShouldBeGreaterThan(0);
        if (cancel) { complete.ShouldBe(0); }
        else { complete.ShouldBeGreaterThan(0); }
        _output.WriteLine($"{durability}, canceled={cancel}: crash images={recorder.Count}; absent={absent}; complete={complete}; checkpoints={checkpoints}");
    }
}
