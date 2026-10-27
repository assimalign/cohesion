using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlAlterColumnTypeReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: every crash image recovers complete definitions, rows and indexes")]
    [InlineData(StorageCommitDurability.Synchronous, false)]
    [InlineData(StorageCommitDurability.Grouped, false)]
    [InlineData(StorageCommitDurability.None, false)]
    [InlineData(StorageCommitDurability.Synchronous, true)]
    [InlineData(StorageCommitDurability.Grouped, true)]
    [InlineData(StorageCommitDurability.None, true)]
    public async Task Reopen_TypeCrashImages_ShouldRecoverCompletePublication(StorageCommitDurability durability, bool cancel)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var storage = new CrashCaptureSqlStorageStrategy(durability);
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        ulong old, prepared = 0;
        int checkpoints = 0;
        await using (var engine = SqlDatabaseEngine.Create("type-crash", new() { StorageStrategy = storage, Durability = durability }))
        {
            var database = await engine.CreateDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await ExecAsync(session, "CREATE TABLE t(id INT PRIMARY KEY, v INT DEFAULT 7 UNIQUE CHECK(v>0))");
            await ExecAsync(session, "INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(1, 65).Select(n => $"({n},{n})")));
            old = Table(database).ObjectId;
            long epoch = database.TableReclamation.EnterStatement();
            SqlPlanExecutor.RewriteStage = (stage, value) =>
            {
                if (stage == "Reserved") { prepared = checked((ulong)value); }
                if (stage != "Copied") { return; }
                database.Coordinator.Checkpoint(timeout.Token);
                database.CatalogStorage.Checkpoint();
                checkpoints++;
                if (cancel) { cancellation.Cancel(); }
            };
            try
            {
                // Act: record through copy checkpoints, publication/abandonment and reclamation.
                using (recorder = storage.RecordCrashPoints())
                {
                    if (cancel)
                    {
                        await Should.ThrowAsync<OperationCanceledException>(async () =>
                            await session.ExecuteAsync("ALTER TABLE t ALTER v TYPE BIGINT USING v+1000", cancellationToken: cancellation.Token));
                        database.Catalog.GetPendingTableReclamations().ShouldContain(prepared);
                    }
                    else { await session.ExecuteAsync("ALTER TABLE t ALTER v TYPE BIGINT USING v+1000", cancellationToken: timeout.Token); }
                    database.TableReclamation.ExitStatement(epoch);
                    epoch = 0;
                    await ReclaimAsync(database, timeout.Token);
                    database.Coordinator.Checkpoint(timeout.Token);
                    database.CatalogStorage.Checkpoint();
                }
            }
            finally
            {
                SqlPlanExecutor.RewriteStage = null;
                if (epoch != 0) { database.TableReclamation.ExitStatement(epoch); }
            }
        }

        // Assert: each image admits exactly the original or the complete converted table.
        checkpoints.ShouldBe(cancel ? 1 : 2);
        recorder.Count.ShouldBeGreaterThan(1);
        int retained = 0, replaced = 0;
        for (int point = 0; point < recorder.Count; point++)
        {
            await using var recovered = SqlDatabaseEngine.Create("type-recover", new() { StorageStrategy = recorder.Open(point), Durability = durability });
            var database = await recovered.OpenDatabaseAsync("db", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            var table = Table(database);
            bool converted = table.ObjectId != old;
            if (converted) { replaced++; table.ObjectId.ShouldBe(prepared); }
            else { retained++; }
            table.Columns[1].DefaultLiteral.ShouldBe("7");
            table.Columns[1].Type.Type.ShouldBe(converted ? DatabaseType.Int64 : DatabaseType.Int32);
            (await RowsAsync(session, "SELECT v FROM t ORDER BY id")).Select(r => Convert.ToInt64(r[0]))
                .ShouldBe(Enumerable.Range(1, 65).Select(n => (long)n + (converted ? 1000 : 0)));
            database.Catalog.GetIndexes(table.ObjectId).Count.ShouldBe(2);
            long key = converted ? 1001 : 1;
            (await RowsAsync(session, $"SELECT id FROM t WHERE v={key}")).ShouldHaveSingleItem()[0].ShouldBe(1);
            session.LastStatementMetrics!.AccessPath.ShouldStartWith("seek:");
            await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, $"INSERT INTO t VALUES(66,{key})"));
            await ReclaimAsync(database, timeout.Token);
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            database.DataStorage.HasOwnerPages(converted ? old : prepared).ShouldBeFalse();
            database.DataStorage.HasOwnerPages(table.ObjectId).ShouldBeTrue();
        }
        retained.ShouldBeGreaterThan(0);
        if (cancel) { replaced.ShouldBe(0); }
        else { replaced.ShouldBeGreaterThan(0); }
        _output.WriteLine($"TYPE {durability}, canceled={cancel}: images={recorder.Count}, old={retained}, converted={replaced}, checkpoints={checkpoints}");
    }

    private static async Task ReclaimAsync(SqlDatabase database, CancellationToken cancellationToken)
    {
        do
        {
            database.TableReclamation.RunBatch(cancellationToken);
            if (database.TableReclamation.HasWork) { await Task.Delay(1, cancellationToken); }
        } while (database.TableReclamation.HasWork);
    }
}
