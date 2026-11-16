using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - DROP TABLE and DROP COLUMN retire alternate tree owners")]
    [InlineData("table")]
    [InlineData("column")]
    public async Task ExecuteAsync_AlternateOwnerDdlRetirement_ShouldReclaimTreePages(string kind)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        SeedRaw(database, [7, 8], 256);
        await database.ApplySchemaAsync(OwnerDeclaration(true, [new("ix_w", ["w"], false, Predicate: "v > 0")]), guard.Token);
        var registration = database.IndexManager.ExportRegistrations().Single();
        ulong owner = database.IndexManager.PageOwner(registration);
        owner.ShouldNotBe(Table(database).ObjectId);
        IndexOwnerPages(database, owner).ShouldBeGreaterThan(0);
        await DrainReclamationAsync(database, guard.Token);
        // The catalog fixture relinquishes typed ownership to exercise ordinary SQL DDL.
        // The heap, registrations and physical trees are those built by the joint path.
        foreach (var table in database.Catalog.Tables.ToArray())
        {
            var adhoc = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, table.Columns, table.PrimaryKeyColumns,
                DatabaseObjectOwner.Adhoc, null, table.Constraints, table.DroppedColumnOrdinals);
            var indexes = database.Catalog.GetIndexes(table.ObjectId).Select(index => new SqlCatalogIndex(index.TableObjectId,
                index.Name, index.ColumnNames, index.IsUnique, DatabaseObjectOwner.Adhoc, null, index.IsPrimaryKey,
                index.Predicate, index.PredicateColumns, index.PredicateFunctionCalls, index.KeyOrders)).ToArray();
            await database.Catalog.PublishStorageReplacementAsync([], null, [], indexes, database.IndexManager.ExportRegistrations(),
                new(), false, Assimalign.Cohesion.Database.Transactions.TransactionSequence.None, string.Empty, null, guard.Token,
                definitionChange: true, kept: [adhoc], expectedKept: [table]);
        }
        await using var writer = await database.CreateSessionAsync(guard.Token);

        // Act
        await writer.ExecuteAsync(kind switch
        {
            "table" => "DROP TABLE items",
            _ => "ALTER TABLE items DROP COLUMN v",
        }, cancellationToken: guard.Token);
        await DrainReclamationAsync(database, guard.Token);

        // Assert
        IndexOwnerPages(database, owner).ShouldBe(0);
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
        var current = await reopened.OpenDatabaseAsync("typed", guard.Token);
        current.Catalog.GetIndexRegistrations().ShouldBeEmpty();
        if (kind == "column")
        {
            await using var reader = await current.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE w=8", guard.Token)).Single()[0].ShouldBe(256L);
        }
        else { current.Catalog.TryGetTable("dbo", "items", out _).ShouldBeFalse(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint index-only publication skips unchanged outgoing and incoming foreign keys")]
    public async Task ApplyAsync_IndexOnlyWithUnchangedReferences_ShouldDecodeOnlyIndexBuildRows()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declaration(bool changed) => Compiled(OwnerDeclaration(changed, changed ? [new("ix_w", ["w"], false)] : []).Tables.Append(
                new("parent", "parent", [new("v", DatabaseType.Int32, false)], new("pk", ["v"]), [], [])).Append(
                new("child", "child", [new("v", DatabaseType.Int32, true)], null, [],
                    [new("fk_items", CompiledSchemaConstraintKind.Reference, ["v"], "items", ["w"])])).Select(table => table.Name != "items"
                        ? table : new CompiledSchemaTable(table.Name, table.RowType, table.Columns, table.PrimaryKey,
                            table.Indexes.Concat([new CompiledSchemaIndex("uq_w", ["w"], true)]).ToArray(),
                            [new("fk", CompiledSchemaConstraintKind.Reference, ["v"], "parent", ["v"])])).ToArray());
        await database.ApplySchemaAsync(Declaration(false), guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO parent VALUES(7)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("INSERT INTO child VALUES(8)", cancellationToken: guard.Token);
        int otherDecodes = 0, buildDecodes = 0;
        bool building = false;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "BeforeTreeBuild") { building = true; }
            if (stage == "AfterTreeBuild") { building = false; }
        };
        SqlPlanExecutor.RowDecoded = row =>
        {
            if (building) { buildDecodes++; }
            else { otherDecodes++; }
        };
        try
        {
            // Act
            await database.ApplySchemaAsync(Declaration(true), guard.Token);

            // Assert
            otherDecodes.ShouldBe(0);
            buildDecodes.ShouldBe(2);
        }
        finally { SqlPlanExecutor.RowDecoded = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Alternate-tree retirement intents retain their owning table name")]
    public async Task ApplyAsync_AlternateTreeRetirement_ShouldEncodeTableName()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new CrashCaptureSqlStorageStrategy()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(false), guard.Token);
        await database.ApplySchemaAsync(OwnerDeclaration(true), guard.Token);
        await using var reader = await database.CreateSessionAsync(guard.Token);
        await reader.ExecuteAsync("INSERT INTO items VALUES(7,8)", cancellationToken: guard.Token);
        var registration = database.IndexManager.ExportRegistrations().Single();
        ulong owner = database.IndexManager.PageOwner(registration);
        (ulong, string, string)? encoded = null;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != "CatalogPublished") { return; }
            using var iterator = database.CatalogStorage.GetUnitIterator(owner);
            iterator.MoveNext().ShouldBeTrue();
            var reader = new DatabaseKeyReader(iterator.Current.Data.Span);
            reader.ReadInt32().ShouldBe(SqlReclamationRecord.Kind);
            encoded = SqlReclamationRecord.Decode(ref reader);
        };

        // Act
        var refused = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.ApplySchemaAsync(OwnerDeclaration(false, []), guard.Token));
        refused.Diagnostics.ShouldContain(value => value.Code == "CS03105");
        encoded.ShouldBeNull();
        (await RowsAsync(reader, "SELECT v,w FROM items", guard.Token)).Single().ShouldBe([7, 8]);
        // A new opted-in declaration can narrow and retire the old tree. An earlier
        // declaration's opt-in cannot authorize that loss on a historical rollback.
        await database.ApplySchemaAsync(OwnerDeclaration(false, [new("ix_w", ["w"], false)]), guard.Token);

        // Assert
        encoded.ShouldBe((owner, "dbo", "items"));
        (await RowsAsync(reader, "SELECT v,w FROM items", guard.Token)).Single().ShouldBe([7, 8]);
        await DrainReclamationAsync(database, guard.Token);
        IndexOwnerPages(database, owner).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DROP SCHEMA CASCADE retires alternate owners in its namespace")]
    public async Task ExecuteAsync_AlternateOwnerDropSchema_ShouldReclaimTreePages()
    {
        // Arrange: use the same alternate-owner reservation/build/publication path
        // in an ad-hoc namespace, since typed tables live in protected dbo.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE SCHEMA retirement", cancellationToken: guard.Token);
        await writer.ExecuteAsync("CREATE TABLE retirement.items(v INTEGER)", cancellationToken: guard.Token);
        database.Catalog.TryGetTable("retirement", "items", out var table).ShouldBeTrue();
        ulong owner = database.Catalog.ReserveJointTreeOwner(table, guard.Token);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        try
        {
            await database.Coordinator.ApplyStatementAsync(transaction, bracket => database.IndexManager.CreatePreparedAsync(transaction,
                table.ObjectId, owner, new IndexDefinition("ix_v", IndexKind.BTree), guard.Token), durable: true, guard.Token);
            var index = new SqlCatalogIndex(table.ObjectId, "ix_v", ["v"], false);
            await database.Catalog.PublishStorageReplacementAsync([], null, [], [index], database.IndexManager.ExportRegistrations(includePrepared: true),
                new(), false, TransactionSequence.None, string.Empty, null, guard.Token, definitionChange: true,
                kept: [table], expectedKept: [table], treeOwners: [owner], publishTrees: database.IndexManager.PublishPrepared);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
        IndexOwnerPages(database, owner).ShouldBeGreaterThan(0);

        // Act
        await writer.ExecuteAsync("DROP SCHEMA retirement CASCADE", cancellationToken: guard.Token);
        await DrainReclamationAsync(database, guard.Token);

        // Assert
        IndexOwnerPages(database, owner).ShouldBe(0);
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
        var current = await reopened.OpenDatabaseAsync("typed", guard.Token);
        current.Catalog.TryGetTable("retirement", "items", out _).ShouldBeFalse();
        current.Catalog.GetIndexRegistrations().ShouldBeEmpty();
    }
}
