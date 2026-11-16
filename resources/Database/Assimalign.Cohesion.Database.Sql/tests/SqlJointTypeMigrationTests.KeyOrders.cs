using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint direction and NULL-placement replacements keep the heap and publish one new tree (#1777, #1787)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ApplyAsync_KeyOrderOnlyChange_ShouldKeepHeapAndRebuildTree(int bits)
    {
        // Arrange: SQ-A4 independently owns trees added to a kept heap. A second
        // joint migration changes only this table's index order.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false, []), guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(NULL,0),(2,1),(-1,2),(2,3)", cancellationToken: guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(true, [new("ix", ["v"], KeyOrders: [default])]), guard.Token);
        var heap = Table(database);
        var pages = database.DataStorage.GetOwnerPages(heap.ObjectId).ToArray();
        var registration = database.IndexManager.ExportRegistrations().Single();
        ulong oldTreeOwner = database.IndexManager.PageOwner(registration);
        oldTreeOwner.ShouldNotBe(heap.ObjectId);
        var target = OwnerDeclaration(false, [new("ix", ["v"], KeyOrders: [(CompiledSchemaIndexKeyOrder)bits])]);
        int publications = 0;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "Publishing")
            {
                database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Table == "items" && step.Kind == "Joint/KeepOwner");
                database.Catalog.GetIndexes(heap.ObjectId).ShouldHaveSingleItem().KeyOrders.ShouldBe([default(SqlCatalogIndexKeyOrder)]);
            }
            if (stage == "Published") { publications++; }
        };

        // Act
        await database.ApplySchemaAsync(target, guard.Token);

        // Assert: a key-order change must not compare equal or reuse the old tree.
        publications.ShouldBe(1);
        database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        Table(database).ObjectId.ShouldBe(heap.ObjectId);
        database.DataStorage.GetOwnerPages(heap.ObjectId).ShouldBe(pages);
        var current = database.IndexManager.ExportRegistrations().Single();
        database.IndexManager.PageOwner(current).ShouldNotBe(oldTreeOwner);
        current.RootPageId.ShouldNotBe(registration.RootPageId);
        database.Catalog.GetIndexes(heap.ObjectId).ShouldHaveSingleItem().KeyOrders.ShouldBe([(SqlCatalogIndexKeyOrder)bits]);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        await AssertDirectedJointTreeAsync(database, heap.ObjectId, bits, false, guard.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE rewrite retains non-default compiled index key orders and bytes (#1777, #1787)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ApplyAsync_TypeRewriteWithDirectedIndex_ShouldPreserveKeyOrder(int bits)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declaration(bool wide) => Compiled(new CompiledSchemaTable("items", "items",
            [new("v", wide ? DatabaseType.Int64 : DatabaseType.Int32, true), new("w", DatabaseType.Int32, true)], null,
            [new("ix", ["v"], KeyOrders: [(CompiledSchemaIndexKeyOrder)bits])], []));
        await database.ApplySchemaAsync(Declaration(false), guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(NULL,0),(2,1),(-1,2),(2,3)", cancellationToken: guard.Token);
        ulong oldHeap = Table(database).ObjectId;

        // Act
        await database.ApplySchemaAsync(Declaration(true), guard.Token);

        // Assert
        Table(database).ObjectId.ShouldNotBe(oldHeap);
        database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        database.Catalog.GetIndexes(Table(database).ObjectId).ShouldHaveSingleItem().KeyOrders.ShouldBe([(SqlCatalogIndexKeyOrder)bits]);
        await AssertDirectedJointTreeAsync(database, Table(database).ObjectId, bits, true, guard.Token);
    }

    private static async Task AssertDirectedJointTreeAsync(SqlDatabase database, ulong owner, int bits, bool wide, CancellationToken token)
    {
        database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
        database.IndexManager.TryGetIndex(owner, "ix", out var tree).ShouldBeTrue();
        await using var session = await database.CreateSessionAsync(token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, token);
        var actual = new List<object?>();
        await using (var cursor = tree.OpenCursor(transaction.Context, default))
        {
            while (await cursor.MoveNextAsync(token))
            {
                var (page, slot) = SqlRecordLocation.Unpack(cursor.CurrentEntryReference);
                database.DataStorage.TryReadRecord(page, slot, owner, out var record).ShouldBeTrue();
                actual.Add(SqlRowCodec.Decode(record.Span, table, out _, out _, out _)[0]);
            }
        }
        object low = wide ? (object)(-1L) : -1;
        object high = wide ? (object)2L : 2;
        object?[] expected = bits switch
        {
            1 => [low, high, high, null],
            2 => [null, high, high, low],
            _ => [high, high, low, null],
        };
        actual.ShouldBe(expected);
        await transaction.RollbackAsync(token);
    }
}
