using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlPartialIndexSecondPassTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Catalog refuses republishing a dropped physical owner (#1808)")]
    public async Task CreateIndexAsync_DroppedOwner_ShouldRefuseBeforePublication()
    {
        // Arrange: preserve the original public-catalog DROP/republication sequence.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("owner-publication");
        builder.Options.StorageStrategy = storage;
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(k INT PRIMARY KEY)");
        await SqlPartialIndexTests.Execute(writer, "INSERT INTO t VALUES(1),(2)");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX ix ON t(k)");
        var table = database.Catalog.Tables.Single();
        var registrations = database.Catalog.GetIndexRegistrations();
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        await database.Catalog.DropIndexAsync(table.ObjectId, "ix", [], guard.Token);
        long journal = database.CatalogStorage.JournalLength;

        // Act.
        var error = await Should.ThrowAsync<SqlCatalogException>(async () =>
            await database.Catalog.CreateIndexAsync(new SqlCatalogIndex(table.ObjectId, "ix", ["k"], false), registrations, guard.Token));

        // Assert: a refused publication writes nothing and preserves the surviving index.
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05060");
        database.CatalogStorage.JournalLength.ShouldBe(journal);
        database.Catalog.GetIndexes(table.ObjectId).ShouldHaveSingleItem();
        (await SqlPartialIndexTests.Rows(writer, "SELECT k FROM t ORDER BY k")).Select(row => row[0]).ShouldBe([1, 2]);
        (await SqlPartialIndexTests.Rows(writer, "SELECT k FROM t WHERE k=1")).Single()[0].ShouldBe(1);
        await ReadSurvivor(database);
        await database.Coordinator.RollbackAsync(pin, guard.Token);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var next = SqlDatabaseEngine.CreateBuilder("owner-publication-image");
            next.Options.StorageStrategy = storage.CaptureDurableImages();
            await using var reopened = await next.BuildAsync(guard.Token);
            var image = await reopened.OpenDatabaseAsync("db", guard.Token);
            image.Catalog.GetIndexes(table.ObjectId).ShouldHaveSingleItem();
            await using var reader = await image.CreateSessionAsync(guard.Token);
            (await SqlPartialIndexTests.Rows(reader, "SELECT k FROM t WHERE k=2")).Single()[0].ShouldBe(2);
            await ReadSurvivor(image);
        }

        async Task ReadSurvivor(SqlDatabase current)
        {
            var registration = current.Catalog.GetIndexRegistrations().Single();
            current.IndexManager.TryGetIndex(table.ObjectId, registration.Definition.Name, out var tree).ShouldBeTrue();
            var readerPin = await current.Coordinator.BeginSnapshotPinAsync(guard.Token);
            try
            {
                await using var cursor = tree.OpenCursor(readerPin.Snapshot, IndexKeyRange.All, cancellationToken: guard.Token);
                int read = 0;
                while (await cursor.MoveNextAsync(guard.Token)) { read++; }
                read.ShouldBe(2);
            }
            finally { await current.Coordinator.RollbackAsync(readerPin, guard.Token); }
        }
    }
}
