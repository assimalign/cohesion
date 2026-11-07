using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSchemaRollbackPolicyTests
{
    private readonly ITestOutputHelper _output;

    public SqlSchemaRollbackPolicyTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Explicit rollback: history refuses opted-in deletion and dropped-column re-add on standalone and held databases")]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ApplySchemaAsync_EarlierDeclaration_ShouldRefuseAndPreserveRows(bool readd, bool optIn, bool declared)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = Shape(readd, optIn);
        var second = Shape(!readd, true);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first.Compile(), guard.Token);
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync(readd ? "INSERT INTO items VALUES(1,42)" : "INSERT INTO items VALUES(1,42,9)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        await using var engine = await Builder(storage, declared ? first : null).BuildAsync(guard.Token);
        var held = await engine.OpenDatabaseAsync("history", guard.Token);
        var progress = held.Provisioning;
        ulong generation = held.Catalog.DdlGeneration;

        // Act
        Exception? observed = null;
        try { await held.ApplySchemaAsync(first.Compile(), guard.Token); }
        catch (SqlSchemaMigrationException failure) { observed = failure; }
        _output.WriteLine($"Explicit readd={readd}, optIn={optIn}, declared={declared}: {observed?.Message ?? "APPLIED"}; hash={held.Catalog.SchemaState!.ContentHash}");
        held.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
        _output.WriteLine($"Added exists: {table.FindColumn("Added") is not null}");
        await using var reader = await held.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(42);
        if (!readd)
        { (await RowsAsync(reader, "SELECT Added FROM items", guard.Token)).Single()[0].ShouldBe(9); }

        if (readd && table.FindColumn("Added") is not null)
        { _output.WriteLine("Re-added Added value: " + ((await RowsAsync(reader, "SELECT Added FROM items", guard.Token)).Single()[0] ?? "NULL")); }

        // Assert
        var refused = observed.ShouldBeOfType<SqlSchemaMigrationException>();
        refused.Diagnostics.ShouldContain(value => value.Code == "CS03105");
        refused.Message.ShouldContain(readd ? "AddColumn 'items.Added'" : "DropColumn 'items.Added'", Case.Sensitive);
        held.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
        held.Catalog.DdlGeneration.ShouldBe(generation);
        held.Catalog.PendingMigration.ShouldBeNull();
        if (declared) { held.Provisioning.ShouldBeSameAs(progress); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback validation: CHECK and FK hold on inline, scripted and deferred Build with surviving rows")]
    [InlineData("check", "inline")]
    [InlineData("check", "script")]
    [InlineData("check", "deferred")]
    [InlineData("fk", "inline")]
    [InlineData("fk", "script")]
    [InlineData("fk", "deferred")]
    public async Task BuildAsync_ValidatingRollback_ShouldHoldWithoutRunning(string kind, string route)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Version(bool constrained) => SqlSchema.Create("history", schema =>
        {
            schema.Table<Parent>("parents", table => table.Key(row => row.Id));
            schema.Table<Row>("items", table =>
            {
                table.Key(row => row.Id); table.Column(row => row.Value);
                if (!constrained) { table.Index(row => row.Value); }
                if (constrained && kind == "check") { table.Check("ck_positive", "Value > 0"); }
                if (constrained && kind == "fk") { table.References<Parent>(row => row.Value); }
            });
        });
        var first = Version(true);
        var second = Version(false);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first.Compile(), guard.Token);
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,-1)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        builder.AddDatabase("history", declaration =>
        {
            declaration.Schema(first);
            declaration.RunMigrationsInline = route == "inline";
            if (route == "script") { declaration.Script("probe", "CREATE FUNCTION rollback_probe() RETURNS INT RETURN 42"); }
        });

        // Act
        await using var engine = await builder.BuildAsync(guard.Token);
        var held = await engine.OpenDatabaseAsync("history", guard.Token);
        while (held.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, guard.Token); }
        _output.WriteLine($"{kind}/{route}: {held.Provisioning?.FailureCode ?? "APPLIED"}");

        // Assert
        held.IsOffline.ShouldBeFalse();
        held.Provisioning!.FailureCode.ShouldBe("CS03405");
        held.Provisioning.Detail!.ShouldContain("AddConstraint", Case.Sensitive);
        held.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
        held.Catalog.GetIndexes(table.ObjectId).ShouldContain(index => index.Name == "IX_items_Value");
        held.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
        held.Catalog.PendingMigration.ShouldBeNull();
        await using var reader = await held.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(-1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rollback validation: unique index holds before inline or deferred preparation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_UniqueRollback_ShouldHoldDuplicateRows(bool deferred)
    {
        // Arrange: the compiled metadata surface also supports UNIQUE, beyond the typed builder.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("history", guard.Token);
        var second = Shape(false, false).Compile();
        var table = second.Tables.Single();
        var first = new SqlCompiledSchema(second.Format, second.Name, false, second.Types,
            [new CompiledSchemaTable(table.Name, table.RowType, table.Columns, table.PrimaryKey,
                [new CompiledSchemaIndex("ux_value", ["Value"], true)],
                [])], []);
        await database.ApplySchemaAsync(first, guard.Token);
        await database.ApplySchemaAsync(second, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,42),(2,42)", cancellationToken: guard.Token);

        // Act: use the same internal Build policy for compiled-only metadata.
        await database.SchemaProvisioner.ApplyAsync(first, guard.Token, deferDataScalingSteps: deferred, holdUnsafeChanges: true);
        while (database.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, guard.Token); }

        // Assert
        database.Provisioning!.FailureCode.ShouldBe("CS03405");
        database.Provisioning.Detail!.ShouldContain("AddIndex", Case.Sensitive);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(second.Hash);
        (await RowsAsync(writer, "SELECT Value FROM items ORDER BY Id", guard.Token)).Select(row => row[0]).ShouldBe(new object?[] {42,42});
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rollback table re-add: hold the missing table and clear when the stored declaration returns")]
    public async Task BuildAsync_DroppedTableRollback_ShouldHoldAndAllowRedeploy()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Version(bool archive) => SqlSchema.Create("history", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Table<Row>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); });
            if (archive) { schema.Table<Parent>("archive", table => table.Key(row => row.Id)); }
        });
        var first = Version(true); var second = Version(false);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first.Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,42)", cancellationToken: guard.Token);
            await writer.ExecuteAsync("INSERT INTO archive VALUES(9)", cancellationToken: guard.Token);
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
            storage = storage.CaptureDurableImages();
        }
        // Act
        await using (var old = await Builder(storage, first).BuildAsync(guard.Token))
        {
            var held = await old.OpenDatabaseAsync("history", guard.Token);
            // Assert
            held.Provisioning.ShouldNotBeNull().FailureCode.ShouldBe("CS03405");
            held.Provisioning.Detail!.ShouldContain("AddTable 'archive'", Case.Sensitive);
            held.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
            await using var reader = await held.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(42);
            storage = storage.CaptureDurableImages();
        }
        await using var current = await Builder(storage, second).BuildAsync(guard.Token);
        (await current.OpenDatabaseAsync("history", guard.Token)).Provisioning.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Roll-forward: first-applied generations order two and three version narrowing cycles")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_LaterAppliedDeclaration_ShouldRollForward(bool threeVersions)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = Numeric(0); var second = Numeric(1); var third = Numeric(2);
        async Task Deploy(SqlSchema schema)
        {
            await using var engine = await Builder(storage, schema).BuildAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("history", guard.Token);
            while (database.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, guard.Token); }
            database.Provisioning.ShouldBeNull();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(schema.Compile().Hash);
            await using var reader = await database.CreateSessionAsync(guard.Token);
            var rows = await RowsAsync(reader, "SELECT Value FROM items", guard.Token);
            if (rows.Length == 0) { await reader.ExecuteAsync("INSERT INTO items VALUES(1,42)", cancellationToken: guard.Token); }
            else { Convert.ToInt64(rows.Single()[0]).ShouldBe(42); }
            storage = storage.CaptureDurableImages();
        }
        await Deploy(first); await Deploy(second);
        if (threeVersions) { await Deploy(third); }
        // Act / Assert
        await Deploy(threeVersions ? second : first);
        await Deploy(threeVersions ? third : second);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Held Build suspends decision 65 validation and skips scripts while stored definitions still govern writes")]
    public async Task BuildAsync_HeldWithRemovedFunction_ShouldServeRowsAndSkipScripts()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = Shape(false, false);
        var second = SqlSchema.Create("history", schema => schema.Table<Row>("items", table =>
        { table.Key(row => row.Id); table.Column(row => row.Value); table.Column(row => row.Added); table.Check("ck_retired", "retired(Value)"); }));
        var seedBuilder = Builder(storage);
        seedBuilder.Functions.Add(SqlScalarFunction.Create<int, bool>("retired", value => value > 0, SqlFunctionVolatility.Immutable));
        await using (var seed = await seedBuilder.BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first.Compile(), guard.Token);
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,42,9)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        builder.AddDatabase("history", declaration => { declaration.Schema(first); declaration.Script("probe", "CREATE FUNCTION rollback_probe() RETURNS INT RETURN 42"); });
        // Act
        await using var engine = await builder.BuildAsync(guard.Token);
        var held = await engine.OpenDatabaseAsync("history", guard.Token);
        // Assert
        held.Provisioning!.FailureCode.ShouldBe("CS03405");
        held.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
        await using var reader = await held.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Added FROM items", guard.Token)).Single()[0].ShouldBe(9);
        var writeFailure = await Should.ThrowAsync<DatabaseException>(async () => await reader.ExecuteAsync("INSERT INTO items VALUES(2,42,9)", cancellationToken: guard.Token));
        writeFailure.Diagnostics.ShouldContain(value => value.Code == "CS01009");
        await Should.ThrowAsync<DatabaseException>(async () => await reader.ExecuteAsync("SELECT rollback_probe()", cancellationToken: guard.Token));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Index-only rollback: adding a non-unique index or dropping an index publishes and preserves rows")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_IndexOnlyRollback_ShouldPublishAndPreserveRows(bool addIndex, bool inline)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Version(bool index) => SqlSchema.Create("history", schema => schema.Table<Row>("items", table =>
        { table.Key(row => row.Id); table.Column(row => row.Value); if (index) { table.Index(row => row.Value); } }));
        var first = Version(addIndex); var second = Version(!addIndex);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first.Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,42)", cancellationToken: guard.Token);
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        builder.AddDatabase("history", declaration => { declaration.Schema(first); declaration.RunMigrationsInline = inline; });
        // Act
        await using var engine = await builder.BuildAsync(guard.Token);
        var restored = await engine.OpenDatabaseAsync("history", guard.Token);
        while (restored.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, guard.Token); }
        // Assert
        restored.Provisioning.ShouldBeNull();
        restored.Catalog.SchemaState!.ContentHash.ShouldBe(first.Compile().Hash);
        await using var reader = await restored.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Value FROM items", guard.Token)).Single()[0].ShouldBe(42);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Explicit rollback: an old opt-in never deletes a newer table or narrows newer values")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApplySchemaAsync_EarlierTableOrType_ShouldRefuseNewerValues(bool typeChange, bool declared)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        SqlSchema Version(bool newer) => typeChange ? Numeric(newer ? 0 : 1) : SqlSchema.Create("history", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Table<Row>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); });
            if (newer) { schema.Table<Parent>("archive", table => table.Key(row => row.Id)); }
        });
        var first = Version(false); var second = Version(true);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first.Compile(), guard.Token);
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync(typeChange ? "INSERT INTO items VALUES(1,2147483648)" : "INSERT INTO archive VALUES(9)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        await using var engine = await Builder(storage, declared ? first : null).BuildAsync(guard.Token);
        var held = await engine.OpenDatabaseAsync("history", guard.Token);
        var progress = held.Provisioning;
        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await held.ApplySchemaAsync(first.Compile(), guard.Token));
        // Assert
        failure.Diagnostics.ShouldContain(value => value.Code == "CS03105");
        failure.Message.ShouldContain(typeChange ? "AlterColumn 'items.Value'" : "DropTable 'archive'", Case.Sensitive);
        held.Catalog.SchemaState!.ContentHash.ShouldBe(second.Compile().Hash);
        held.Catalog.PendingMigration.ShouldBeNull();
        if (declared) { held.Provisioning.ShouldBeSameAs(progress); }
        await using var reader = await held.CreateSessionAsync(guard.Token);
        Convert.ToInt64((await RowsAsync(reader, typeChange ? "SELECT Value FROM items" : "SELECT Id FROM archive", guard.Token)).Single()[0])
            .ShouldBe(typeChange ? 2147483648L : 9L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Held rewrite: a storage latch during cleanup preserves the hold and permits reopen with surviving rows")]
    public async Task RewriteTableAsync_HeldCleanupGoesOffline_ShouldPreserveHoldAndReopen()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        var first = Shape(false, false); var second = Shape(true, true);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("history", guard.Token);
            await database.ApplySchemaAsync(first.Compile(), guard.Token);
            await database.ApplySchemaAsync(second.Compile(), guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1,42,9)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        await using var engine = await Builder(storage, first).BuildAsync(guard.Token);
        var held = await engine.OpenDatabaseAsync("history", guard.Token);
        var progress = held.Provisioning;
        int latches = 0;
        held.SchemaProvisioner.RewriteRolledBack = () =>
        {
            latches++;
            held.CatalogStorage.TakeOffline(global::Assimalign.Cohesion.Database.Storage.StorageOfflineCause.CheckpointFailures,
                "rewrite cleanup probe", new System.IO.IOException("rewrite cleanup probe"));
        };
        // Act
        await held.RewriteTableAsync("dbo", "items", guard.Token);
        // Assert
        latches.ShouldBe(1);
        held.IsOffline.ShouldBeTrue();
        held.Provisioning.ShouldBeSameAs(progress);
        var reopened = await engine.OpenDatabaseAsync("history", guard.Token);
        reopened.Provisioning!.FailureCode.ShouldBe("CS03405");
        await using var reader = await reopened.CreateSessionAsync(guard.Token);
        (await RowsAsync(reader, "SELECT Added FROM items", guard.Token)).Single()[0].ShouldBe(9);
    }

    private static SqlSchema Numeric(int version) => SqlSchema.Create("history", schema =>
    {
        schema.AllowDestructiveChanges();
        if (version == 0) { schema.Table<Wide>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); }); }
        else if (version == 1) { schema.Table<Row>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); }); }
        else { schema.Table<Small>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); }); }
    });

    private static SqlSchema Shape(bool extra, bool optIn) => SqlSchema.Create("history", schema =>
    {
        if (optIn) { schema.AllowDestructiveChanges(); }
        schema.Table<Row>("items", table => { table.Key(row => row.Id); table.Column(row => row.Value); if (extra) { table.Column(row => row.Added); } });
    });

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy storage, SqlSchema? schema = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("history");
        builder.Options.StorageStrategy = storage;
        if (schema is not null) { builder.AddDatabase(schema); }
        return builder;
    }

    private static async Task<object?[][]> RowsAsync(SqlDatabaseSession session, string sql, CancellationToken token)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: token);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(token)) { rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }

    private sealed record Row(int Id, int Value, int? Added);
    private sealed record Parent(int Id);
    private sealed record Wide(int Id, long Value);
    private sealed record Small(int Id, short Value);
}
