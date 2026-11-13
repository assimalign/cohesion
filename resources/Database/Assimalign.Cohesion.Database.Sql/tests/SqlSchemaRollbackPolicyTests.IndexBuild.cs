using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSchemaRollbackPolicyTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback index build: oversized newer TEXT holds Build and keeps serving across reopen (#1779)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_OversizedRollbackIndex_ShouldHoldAndServeBothRows(bool inline)
    {
        // Arrange: the review's public-API deployment sequence.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Version(bool indexed) => SqlSchema.Create("history", schema => schema.Table<IndexTextRow>("items", table =>
        {
            table.Key(row => row.Id); table.Column(row => row.Value);
            if (indexed) { table.Index(row => row.Value); }
        }));
        var first = Version(true); var second = Version(false);
        string oversized = new('x', 3000);
        await using (var seed = await Builder(storage, first).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("history", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,'short')", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        await using (var newer = await Builder(storage, second).BuildAsync(guard.Token))
        {
            var database = await newer.OpenDatabaseAsync("history", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync($"INSERT INTO items VALUES(2,'{oversized}')", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }

        // Act / Assert: both startup modes hold synchronously, before a worker can build.
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var builder = Builder(storage);
            builder.AddDatabase("history", declaration => { declaration.Schema(first); declaration.RunMigrationsInline = inline; });
            await using var engine = await builder.BuildAsync(guard.Token);
            var held = await engine.OpenDatabaseAsync("history", guard.Token);
            held.IsOffline.ShouldBeFalse();
            held.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("CS03405");
            string detail = held.Provisioning.Detail.ShouldNotBeNull();
            detail.ShouldContain("AddIndex 'items.IX_items_Value'", Case.Sensitive);
            detail.ShouldContain(first.Compile().Hash, Case.Sensitive);
            detail.ShouldContain(second.Compile().Hash, Case.Sensitive);
            held.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
            held.Catalog.PendingMigration.ShouldBeNull();
            held.SchemaProvisioner.Deferred.ShouldBeNull();
            await using var reader = await held.CreateSessionAsync(guard.Token);
            var rows = await RowsAsync(reader, "SELECT Id,Value FROM items ORDER BY Id", guard.Token);
            rows.Length.ShouldBe(2);
            rows[0].ShouldBe(new object?[] { 1, "short" });
            rows[1].ShouldBe(new object?[] { 2, oversized });
            // A second request on the same live engine proves that it remains usable.
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(2L);
            storage = storage.CaptureDurableImages();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback partial index: reject before any step or predicate runs, preserving hashes and rows (#1779)")]
    [InlineData("inline")]
    [InlineData("deferred")]
    [InlineData("explicit")]
    public async Task ApplySchemaAsync_FailingRollbackPredicate_ShouldRefuseWholePlan(string route)
    {
        // Arrange: compiled metadata supports predicates beyond the typed declaration builder.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var shape = Shape(false, false).Compile();
        var table = shape.Tables.Single();
        SqlCompiledSchema Version(bool first) => new(shape.Format, shape.Name, false, shape.Types,
            [new CompiledSchemaTable(table.Name, table.RowType, table.Columns, table.PrimaryKey,
                first ? [new CompiledSchemaIndex("z_restored", ["Value"], Predicate: "10 / Value > 0")]
                    : [new CompiledSchemaIndex("a_newer", ["Id"])], table.Constraints)], []);
        var first = Version(true); var second = Version(false);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first, guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,1)", cancellationToken: guard.Token);
            await database.ApplySchemaAsync(second, guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(2,0)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        await using (var engine = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("history", guard.Token);
            ulong generation = database.Catalog.DdlGeneration;
            var history = database.Catalog.AppliedSchemaHistory.ToArray();
            int preparationStages = 0;
            database.SchemaProvisioner.JointStage = _ => preparationStages++;

            // Act: exercise Build's hold policy for compiled-only shapes, and the public explicit API.
            if (route == "explicit")
            {
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(first, guard.Token));
                failure.Diagnostics.ShouldContain(value => value.Code == "CS03105");
                failure.Message.ShouldContain("AddIndex 'items.z_restored'", Case.Sensitive);
                failure.Message.ShouldContain(first.Hash, Case.Sensitive);
                failure.Message.ShouldContain(second.Hash, Case.Sensitive);
            }
            else
            {
                await database.SchemaProvisioner.ApplyAsync(first, guard.Token,
                    deferDataScalingSteps: route == "deferred", holdUnsafeChanges: true);
                database.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("CS03405");
                database.Provisioning.Detail!.ShouldContain("AddIndex 'items.z_restored'", Case.Sensitive);
            }

            // Assert: the otherwise Safe DropIndex prefix did not run either.
            preparationStages.ShouldBe(0);
            database.SchemaProvisioner.Deferred.ShouldBeNull();
            database.Catalog.DdlGeneration.ShouldBe(generation);
            database.Catalog.AppliedSchemaHistory.ShouldBe(history);
            await AssertPredicateRowsAsync(database, second.Hash, guard.Token);
            storage = storage.CaptureDurableImages();
        }
        await using var reopened = await Builder(storage).BuildAsync(guard.Token);
        await AssertPredicateRowsAsync(await reopened.OpenDatabaseAsync("history", guard.Token), second.Hash, guard.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback index bounds: sum complete composite keys, restore fitting INT and VARCHAR/CHAR keys (#1779)")]
    [InlineData("INT", false, false, 0)]
    [InlineData("VARCHAR(255)", false, false, 0)]
    [InlineData("CHAR(255)", false, false, 0)]
    [InlineData("VARCHAR(256)", false, true, 0)]
    [InlineData("VARCHAR(128)", true, true, 0)]
    [InlineData("VARCHAR(85)", false, false, 3)]
    [InlineData("VARCHAR(86)", false, true, 3)]
    public async Task ApplyAsync_RollbackIndexBound_ShouldHoldOnlyUnprovenKeys(string sqlType, bool composite, bool expectedHold, int collationId)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var shape = SqlSchema.Compile("history", schema => schema.Table<IndexTextRow>("items", table =>
        {
            table.Key(row => row.Id); table.Column(row => row.Value, sqlType == "INT" ? "VARCHAR(255)" : sqlType);
            if (composite) { table.Column(row => row.Other, sqlType); }
        }));
        var table = shape.Tables.Single();
        var first = new SqlCompiledSchema(shape.Format, shape.Name, false, shape.Types,
            [new CompiledSchemaTable(table.Name, table.RowType, table.Columns, table.PrimaryKey,
                [new CompiledSchemaIndex("restored", composite ? ["Value", "Other"] : [sqlType == "INT" ? "Id" : "Value"],
                    KeyOrders: composite ? [CompiledSchemaIndexKeyOrder.AscendingNullsLast, CompiledSchemaIndexKeyOrder.DescendingNullsFirst]
                        : [CompiledSchemaIndexKeyOrder.DescendingNullsLast])], [])], []);
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("history", Collation.FromId((byte)collationId), guard.Token);
        await database.ApplySchemaAsync(first, guard.Token);
        await database.ApplySchemaAsync(shape, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        string value = string.Concat(Enumerable.Repeat(collationId == 3 ? "\U00016D6A" : "\U0010FFFF",
            collationId == 3 ? 85 : composite ? 128 : 255));
        await writer.ExecuteAsync($"INSERT INTO items VALUES(1,'{value}'{(composite ? ",'" + value + "'" : "")})", cancellationToken: guard.Token);

        // Act
        await database.SchemaProvisioner.ApplyAsync(first, guard.Token, holdUnsafeChanges: true);

        // Assert
        if (expectedHold)
        {
            database.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("CS03405");
            database.Provisioning.Detail!.ShouldContain("AddIndex 'items.restored'", Case.Sensitive);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(shape.Hash);
        }
        else
        {
            database.Provisioning.ShouldBeNull();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(first.Hash);
        }
        (await RowsAsync(writer, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(value);
    }

    private static async Task AssertPredicateRowsAsync(SqlDatabase database, string hash, CancellationToken token)
    {
        database.IsOffline.ShouldBeFalse();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
        database.Catalog.GetIndexes(table.ObjectId).ShouldContain(index => index.Name == "a_newer");
        database.Catalog.GetIndexes(table.ObjectId).ShouldNotContain(index => index.Name == "z_restored");
        await using var reader = await database.CreateSessionAsync(token);
        (await RowsAsync(reader, "SELECT Value FROM items ORDER BY Id", token)).Select(row => row[0]).ShouldBe(new object?[] { 1, 0 });
    }

    private sealed record IndexTextRow(int Id, string Value, string Other);
}
