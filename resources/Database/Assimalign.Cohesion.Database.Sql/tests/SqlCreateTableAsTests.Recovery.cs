using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCreateTableAsTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS: destination conversion after a durable batch leaves no table in process or after crash")]
    public async Task ExecuteAsync_DestinationConversionAfterDurableBatch_ShouldReclaimInProcessAndAfterCrash()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("ctas-destination-conversion", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await SeedAsync(session, timeout.Token);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var statement = new SqlStatementContext(transaction, database.Coordinator);
        var source = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "SELECT CASE WHEN id=65 THEN 'bad' ELSE '1' END AS n FROM src ORDER BY id").Statement.SqlExpression);
        // Inject the assignment mismatch at the destination plan, after a valid TEXT
        // source has fully materialized. No source expression performs this conversion.
        var plan = new SqlCreateTableAsPlan("dbo", "copy", [new SqlCatalogColumn("n", new DatabaseTypeInfo(DatabaseType.Int32))], source, true, false);
        var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
        CrashCaptureSqlStorageStrategy? crash = null;
        ulong owner = 0;
        int copied = 0;
        long epoch = database.TableReclamation.EnterStatement();
        SqlPlanExecutor.TableAsStage = (stage, id) =>
        {
            if (stage != "Copied") { return; }
            copied++;
            owner = id;
            // Force the first nonfinal bracket through the durable checkpoint before
            // conversion of row 65; capture the exact failure-point crash image.
            database.Coordinator.Checkpoint(timeout.Token);
            database.CatalogStorage.Checkpoint();
            database.DataStorage.HasOwnerPages(owner).ShouldBeTrue();
            int rows = 0;
            using (var iterator = database.DataStorage.GetUnitIterator(owner))
            { while (iterator.MoveNext()) { rows++; } }
            rows.ShouldBe(64);
            crash = strategy.CaptureDurableImages();
        };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(() => executor.ExecuteAsync(plan, statement, timeout.Token));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02022");
            database.Catalog.GetPendingTableReclamations().ShouldContain(owner);
        }
        finally
        {
            SqlPlanExecutor.TableAsStage = null;
            await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
            database.TableReclamation.ExitStatement(epoch);
            // This direct executor probe also supplies the session's end-of-statement
            // discovery request; the production session owns both epoch and request.
            database.TableReclamation.Request();
        }

        // Assert
        copied.ShouldBe(1);
        statement.Metrics.HashMemoryBytes.ShouldBe(0);
        database.Catalog.TryGetTable("dbo", "copy", out _).ShouldBeFalse();
        await ReclaimAsync(database, timeout.Token);
        database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await using var recovered = SqlDatabaseEngine.Create("ctas-conversion-recover", new() { StorageStrategy = crash.ShouldNotBeNull() });
        var reopened = await recovered.OpenDatabaseAsync("db", timeout.Token);
        reopened.Catalog.TryGetTable("dbo", "copy", out _).ShouldBeFalse();
        await ReclaimAsync(reopened, timeout.Token);
        reopened.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
        reopened.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await using var retry = await reopened.CreateSessionAsync(timeout.Token);
        await ExecAsync(retry, "CREATE TABLE copy AS SELECT id FROM src", timeout.Token);
        (await RowsAsync(retry, "SELECT * FROM copy", timeout.Token)).Count.ShouldBe(65);
    }

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
