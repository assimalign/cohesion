using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCatalogCommitFailureTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Unconfirmed catalog commit: both database file sets stay offline until recovery")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_CatalogDurabilityWaitFails_ShouldFenceDatabaseAndRecover(bool schemaState)
    {
        // Arrange: the grouped callback runs only after the commit record is appended.
        // Keep its bytes to exercise recovery's committed outcome deterministically.
        var strategy = new FaultInjectingJournalSqlStorageStrategy(durable: true);
        await using var engine = SqlDatabaseEngine.Create("catalog-fail-stop", new SqlDatabaseEngineOptions
        {
            StorageStrategy = strategy,
            Durability = StorageCommitDurability.Grouped,
        });
        var database = await engine.CreateDatabaseAsync("catalog-failure", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        ulong generation = database.Catalog.DdlGeneration;
        var target = new SqlCatalogSchemaState("target", new string('x', 20_000), generation);
        int pending = 0;
        database.CatalogStorage.OnCommitPending = () =>
        {
            database.CatalogStorage.FlushPendingCommits();
            pending++;
            if (schemaState || pending == 2)
            {
                throw new NotSupportedException("Injected grouped durability wait failure after the commit record.");
            }
        };

        // Act
        if (schemaState)
        {
            (await Should.ThrowAsync<StorageOfflineException>(async () =>
                await database.Catalog.SaveSchemaStateAsync(target, CancellationToken.None)))
                .CommitRecordWritten.ShouldBeTrue();
        }
        else
        {
            await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await session.ExecuteAsync("CREATE TABLE t_first (id INT NOT NULL)", cancellationToken: CancellationToken.None));
        }

        // Assert: every session and both file sets are fenced; a retry cannot reuse cached state.
        pending.ShouldBe(schemaState ? 1 : 2);
        database.IsOffline.ShouldBeTrue();
        database.DataStorage.IsOffline.ShouldBeTrue();
        database.CatalogStorage.IsOffline.ShouldBeTrue();
        engine.OfflineDatabases.ShouldContain(database.Name);
        await Should.ThrowAsync<DatabaseOfflineException>(async () =>
            await session.ExecuteAsync("CREATE TABLE retry (id INT)", cancellationToken: CancellationToken.None));

        // Act / Assert: reopen recovers the actual catalog before accepting another DDL.
        var reopened = await engine.OpenDatabaseAsync("catalog-failure", CancellationToken.None);
        reopened.IsOffline.ShouldBeFalse();
        reopened.Catalog.DdlGeneration.ShouldBe(generation + (schemaState ? 0UL : 2UL));
        if (schemaState)
        {
            reopened.Catalog.SchemaState.ShouldNotBeNull().CanonicalDocument.ShouldBe(target.CanonicalDocument);
            await reopened.Catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState("next", "next", generation), CancellationToken.None);
            SqlCatalog.Open(reopened.CatalogStorage).SchemaState.ShouldNotBeNull().ContentHash.ShouldBe("next");
        }
        else
        {
            reopened.Catalog.TryGetTable("dbo", "t_first", out var first).ShouldBeTrue();
            first.ObjectId.ShouldBe(1UL);
            await using var next = await reopened.CreateSessionAsync(CancellationToken.None);
            await next.ExecuteAsync("CREATE TABLE t_second (id INT NOT NULL)", cancellationToken: CancellationToken.None);
            reopened.Catalog.TryGetTable("dbo", "t_second", out var second).ShouldBeTrue();
            second.ObjectId.ShouldBe(2UL);
            reopened.Catalog.DdlGeneration.ShouldBe(generation + 4);
        }
    }
}
