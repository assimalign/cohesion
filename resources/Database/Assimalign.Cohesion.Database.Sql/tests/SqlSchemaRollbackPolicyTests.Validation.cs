using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSchemaRollbackPolicyTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback validation: exact TYPE widening holds when retained CHECK overloads can reject newer rows")]
    [InlineData("inline")]
    [InlineData("deferred")]
    [InlineData("script")]
    [InlineData("explicit")]
    public async Task BuildAsync_WideningWithRetainedCheck_ShouldRefuseValidation(string route)
    {
        // Arrange: both overloads exist in every engine. The column type chooses its CHECK
        // overload, so lossless storage widening alone cannot prove row validity.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Version(bool wide) => SqlSchema.Create("history", schema =>
        {
            schema.AllowDestructiveChanges();
            if (wide) { schema.Table<Wide>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); table.Check("ck_guard", "rollback_guard(Value)"); }); }
            else { schema.Table<Row>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); table.Check("ck_guard", "rollback_guard(Value)"); }); }
        });
        SqlDatabaseEngineBuilder Configure(CrashCaptureSqlStorageStrategy image)
        {
            var builder = Builder(image);
            builder.Functions.Add(SqlScalarFunction.Create<int, bool>("rollback_guard", value => value >= 0, SqlFunctionVolatility.Immutable));
            builder.Functions.Add(SqlScalarFunction.Create<long, bool>("rollback_guard", value => value < 0, SqlFunctionVolatility.Immutable));
            return builder;
        }
        var first = Version(true); var second = Version(false);
        await using (var seed = await Configure(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first.Compile(), guard.Token);
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,42)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var rollback = Configure(storage);
        if (route != "explicit")
        {
            rollback.AddDatabase("history", declaration =>
            {
                declaration.Schema(first); declaration.RunMigrationsInline = route == "inline";
                if (route == "script") { declaration.Script("probe", "CREATE FUNCTION rollback_probe() RETURNS INT RETURN 42"); }
            });
        }

        // Act
        await using var engine = await rollback.BuildAsync(guard.Token);
        var held = await engine.OpenDatabaseAsync("history", guard.Token);
        if (route == "explicit")
        {
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await held.ApplySchemaAsync(first.Compile(), guard.Token));
            failure.Diagnostics.ShouldContain(value => value.Code == "CS03105");
            failure.Message.ShouldContain("AlterColumn 'items.Value'", Case.Sensitive);
        }
        else
        {
            while (held.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, guard.Token); }
            _output.WriteLine($"Retained CHECK, {route}: {held.Provisioning?.FailureCode ?? "APPLIED"}");
            held.Provisioning!.FailureCode.ShouldBe("CS03405");
            held.Provisioning.Detail!.ShouldContain("AlterColumn 'items.Value'", Case.Sensitive);
        }

        // Assert
        held.IsOffline.ShouldBeFalse();
        held.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
        held.Catalog.PendingMigration.ShouldBeNull();
        await using var reader = await held.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(42);
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback indexes: separately owned non-unique builds and replacements keep the heap without retained CHECK validation (#1808)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_NonUniqueIndexRollback_ShouldKeepHeapWithoutCheckRevalidation(bool replace, bool inline)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = SqlSchema.Create("history", schema => schema.Table<Row>("items", table =>
        {
            table.Key(row => row.Id); table.Column(row => row.Value); table.Index(row => row.Value);
            table.Check("ck_guard", "retained_guard(Value)");
        }));
        var original = first.Compile();
        var second = new SqlCompiledSchema(original.Format, original.Name, false, original.Types,
            original.Tables.Select(table => new CompiledSchemaTable(table.Name, table.RowType, table.Columns,
                table.PrimaryKey, replace ? table.Indexes.Select(index => new CompiledSchemaIndex(index.Name,
                    index.Columns, index.IsUnique, index.Predicate, [CompiledSchemaIndexKeyOrder.DescendingNullsLast])).ToArray() : [],
                table.Constraints)).ToArray(), []);
        SqlDatabaseEngineBuilder Configure(bool oldApplication)
        {
            var builder = Builder(storage);
            builder.Functions.Add(SqlScalarFunction.Create<int, bool>("retained_guard",
                value => oldApplication ? value < 0 : value >= 0, SqlFunctionVolatility.Immutable));
            return builder;
        }
        await using (var seed = await Configure(true).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(original, guard.Token);
            storage = storage.CaptureDurableImages();
        }
        ulong retainedHeap = 0;
        await using (var newer = await Configure(false).BuildAsync(guard.Token))
        {
            var database = await newer.OpenDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(second, guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,42)", cancellationToken: guard.Token);
            database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
            retainedHeap = table.ObjectId;
            if (replace)
            {
                var registration = database.IndexManager.ExportRegistrations().Single(index => index.Definition.Name == "IX_items_Value");
                database.IndexManager.PageOwner(registration).ShouldNotBe(table.ObjectId);
            }
            storage = storage.CaptureDurableImages();
        }
        var rollback = Configure(true);
        rollback.AddDatabase("history", declaration => { declaration.Schema(first); declaration.RunMigrationsInline = inline; });

        // Act
        await using var engine = await rollback.BuildAsync(guard.Token);
        var databaseUnderTest = await engine.OpenDatabaseAsync("history", guard.Token);
        while (databaseUnderTest.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, guard.Token); }

        // Assert
        databaseUnderTest.Provisioning.ShouldBeNull();
        databaseUnderTest.Catalog.SchemaState!.ContentHash.ShouldBe(original.Hash);
        databaseUnderTest.Catalog.TryGetTable("dbo", "items", out var retained).ShouldBeTrue();
        retained.ObjectId.ShouldBe(retainedHeap);
        databaseUnderTest.Catalog.PendingMigration.ShouldBeNull();
        await using var reader = await databaseUnderTest.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(42);
    }

}
