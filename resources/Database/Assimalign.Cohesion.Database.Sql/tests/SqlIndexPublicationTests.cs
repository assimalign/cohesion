using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlIndexPublicationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Index publication: disjoint snapshots retain both trees after a crash")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateIndex_DisjointPublicationSnapshots_ShouldRetainBothRegistrationsAfterCrash(bool publishTable)
    {
        // Arrange: complete both real trees before either catalog description exists.
        // The explicit ordering exercises the publication race without scheduling threads.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("publication", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE a (Value INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE b (Value INT)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "a", out var first).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "b", out var second).ShouldBeTrue();
        var w = new SqlCatalogIndex(first.ObjectId, "W", ["Value"], true);
        var j = new SqlCatalogIndex(second.ObjectId, "J", ["Value"], true);
        await BuildTreeAsync(database, w, timeout.Token);
        await BuildTreeAsync(database, j, timeout.Token);
        var wSnapshot = Snapshot(database, w);
        var jSnapshot = Snapshot(database, j);
        wSnapshot.Length.ShouldBe(1);
        jSnapshot.Length.ShouldBe(1);

        // Act: the second publisher's snapshot cannot contain the first publication.
        await database.Catalog.CreateIndexAsync(w, wSnapshot, timeout.Token);
        if (publishTable)
        {
            await database.Catalog.PublishTableAsync(second, [j], jSnapshot, replaceExisting: true, timeout.Token);
        }
        else
        {
            await database.Catalog.CreateIndexAsync(j, jSnapshot, timeout.Token);
        }
        var crash = storage.CaptureDurableImages();

        // Assert: recovery must attach both trees before any checkpoint can repair them.
        await using var restarted = await Builder(crash).BuildAsync(timeout.Token);
        var recovered = await restarted.OpenDatabaseAsync("publication", timeout.Token);
        await using var writer = await recovered.CreateSessionAsync(timeout.Token);
        await writer.ExecuteAsync("INSERT INTO a VALUES (1)", cancellationToken: timeout.Token);
        await writer.ExecuteAsync("INSERT INTO b VALUES (1)", cancellationToken: timeout.Token);
        await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
            await writer.ExecuteAsync("INSERT INTO a VALUES (1)", cancellationToken: timeout.Token));
        await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
            await writer.ExecuteAsync("INSERT INTO b VALUES (1)", cancellationToken: timeout.Token));
        recovered.Catalog.GetIndexRegistrations().Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Index checkpoint: a stale snapshot retains later publication after a crash")]
    public async Task SaveIndexRegistrations_SnapshotBeforePublication_ShouldRetainNewRegistrationAfterCrash()
    {
        // Arrange: checkpoint captures a directory before the second index publishes.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("publication", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE a (Value INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE UNIQUE INDEX W ON a (Value)", cancellationToken: timeout.Token);
        var checkpointSnapshot = database.IndexManager.ExportRegistrations();
        await session.ExecuteAsync("CREATE TABLE b (Value INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE UNIQUE INDEX J ON b (Value)", cancellationToken: timeout.Token);
        ulong generation = database.Catalog.DdlGeneration;

        // Act: delayed checkpoint persistence arrives after the newer catalog commit.
        await database.Catalog.SaveIndexRegistrationsAsync(checkpointSnapshot, timeout.Token);
        var crash = storage.CaptureDurableImages();

        // Assert
        await using var restarted = await Builder(crash).BuildAsync(timeout.Token);
        var recovered = await restarted.OpenDatabaseAsync("publication", timeout.Token);
        await using var writer = await recovered.CreateSessionAsync(timeout.Token);
        await writer.ExecuteAsync("INSERT INTO b VALUES (1)", cancellationToken: timeout.Token);
        recovered.Catalog.GetIndexRegistrations().Count.ShouldBe(2);
        recovered.Catalog.DdlGeneration.ShouldBe(generation);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Index checkpoint: a stale snapshot cannot resurrect a dropped or replaced tree")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveIndexRegistrations_SnapshotBeforeDrop_ShouldKeepCurrentDirectoryAfterCrash(bool recreate)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("publication", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE a (Value INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE UNIQUE INDEX W ON a (Value)", cancellationToken: timeout.Token);
        var checkpointSnapshot = database.IndexManager.ExportRegistrations();
        await session.ExecuteAsync("DROP INDEX W ON a", cancellationToken: timeout.Token);
        if (recreate)
        {
            await session.ExecuteAsync("INSERT INTO a VALUES (1)", cancellationToken: timeout.Token);
            await session.ExecuteAsync("CREATE UNIQUE INDEX W ON a (Value)", cancellationToken: timeout.Token);
        }
        var current = database.Catalog.GetIndexRegistrations();
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        await database.Catalog.SaveIndexRegistrationsAsync(checkpointSnapshot, timeout.Token);
        var crash = storage.CaptureDurableImages();

        // Assert
        await using var restarted = await Builder(crash).BuildAsync(timeout.Token);
        var recovered = await restarted.OpenDatabaseAsync("publication", timeout.Token);
        recovered.Catalog.GetIndexRegistrations().ShouldBe(current);
        recovered.Catalog.DdlGeneration.ShouldBe(generation);
        if (recreate)
        {
            await using var writer = await recovered.CreateSessionAsync(timeout.Token);
            await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
                await writer.ExecuteAsync("INSERT INTO a VALUES (1)", cancellationToken: timeout.Token));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Index publication: a replacement cannot attach its predecessor's tree")]
    public async Task CreateIndex_ReplacementDescriptionFirst_ShouldKeepReplacementTreeAfterCrash()
    {
        // Arrange: the dropped and replacement indexes share table, name and uniqueness,
        // but index different values in different real trees.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("publication", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE a (OldValue INT, NewValue INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO a VALUES (10, 20)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE UNIQUE INDEX W ON a (OldValue)", cancellationToken: timeout.Token);
        var stale = database.IndexManager.ExportRegistrations();
        await session.ExecuteAsync("DROP INDEX W ON a", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "a", out var table).ShouldBeTrue();
        var replacement = new SqlCatalogIndex(table.ObjectId, "W", ["NewValue"], true);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await database.Coordinator.ApplyStatementAsync(transaction, async bracket =>
        {
            var tree = await database.IndexManager.CreateIndexAsync(transaction, table.ObjectId,
                new IndexDefinition("W", IndexKind.BTree, IsUnique: true), timeout.Token);
            using var rows = database.DataStorage.GetUnitIterator(table.ObjectId);
            rows.MoveNext().ShouldBeTrue();
            var row = rows.Current;
            var stamps = SqlRowCodec.ReadStamps(row.Data.Span);
            await tree.InsertVersionAsync(bracket, IndexKey.From(new DatabaseKeyWriter().AppendInt32(20)),
                SqlRecordLocation.Pack(row.PageId, row.SlotIndex), stamps.Writer, stamps.Deleter, timeout.Token);
            rows.MoveNext().ShouldBeFalse();
            return true;
        }, durable: true, timeout.Token);
        await database.Coordinator.CommitAsync(transaction, timeout.Token);
        var current = database.IndexManager.ExportRegistrations();
        stale.Single().RootPageId.ShouldBeGreaterThan(0);
        current.Single().RootPageId.ShouldNotBe(stale.Single().RootPageId);

        // Act: description-first publication admits a checkpoint captured before DROP.
        await database.Catalog.CreateIndexAsync(replacement, [], timeout.Token);
        await database.Catalog.SaveIndexRegistrationsAsync(stale, timeout.Token);
        await database.Catalog.SaveIndexRegistrationsAsync(current, timeout.Token);
        var crash = storage.CaptureDurableImages();

        // Assert: recovery must seek and enforce the replacement's key, never the old key.
        await using var restarted = await Builder(crash).BuildAsync(timeout.Token);
        var recovered = await restarted.OpenDatabaseAsync("publication", timeout.Token);
        await using var writer = await recovered.CreateSessionAsync(timeout.Token);
        int newMatches = await CountRowsAsync(writer, "SELECT NewValue FROM a WHERE NewValue = 20", timeout.Token);
        int oldMatches = await CountRowsAsync(writer, "SELECT NewValue FROM a WHERE NewValue = 10", timeout.Token);
        newMatches.ShouldBe(1);
        oldMatches.ShouldBe(0);
        recovered.Catalog.GetIndexRegistrations().ShouldBe(current);
        await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
            await writer.ExecuteAsync("INSERT INTO a VALUES (30, 20)", cancellationToken: timeout.Token));
    }

    private static async Task<int> CountRowsAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(sql, cancellationToken: cancellationToken);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(cancellationToken))
        {
            count++;
        }
        return count;
    }

    private static BTreeIndexRegistration[] Snapshot(SqlDatabase database, SqlCatalogIndex added)
        => database.IndexManager.ExportRegistrations().Where(registration =>
            database.Catalog.TryGetIndex(registration.ObjectId, registration.Definition.Name, out _) ||
            registration.ObjectId == added.TableObjectId && registration.Definition.Name == added.Name).ToArray();

    private static async Task BuildTreeAsync(SqlDatabase database, SqlCatalogIndex index, CancellationToken cancellationToken)
    {
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await database.Coordinator.ApplyStatementAsync(transaction, async _ =>
            await database.IndexManager.CreateIndexAsync(transaction, index.TableObjectId,
                new IndexDefinition(index.Name, IndexKind.BTree, index.IsUnique), cancellationToken),
            durable: true, cancellationToken);
        await database.Coordinator.CommitAsync(transaction, cancellationToken);
    }

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy storage)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("publication");
        builder.Options.StorageStrategy = storage;
        return builder;
    }
}
