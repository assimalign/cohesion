using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlProvisioningCollisionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - B2: table DDL during joint preparation is refused before publication")]
    public async Task ApplySchemaAsync_TableDdlDuringPreparation_ShouldRefuseAndDiscardUnpublishedWork()
    {
        // Arrange: the callback injects the exact interleaving without racing threads.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collisions", CancellationToken.None);
        await using var adhoc = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await adhoc.ExecuteAsync("CREATE TABLE preserved (id INT)", cancellationToken: CancellationToken.None);
        await adhoc.ExecuteAsync("INSERT INTO preserved (id) VALUES (42)", cancellationToken: CancellationToken.None);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "collisions", false, [],
            [Table("a_first"), Table("z_collision")], []);
        int completed = 0;
        database.SchemaProvisioner.BeforeJointPublication = step =>
        {
            completed++;
            step.Operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AddTable);
            step.Operation.ObjectName.ShouldBe("a_first");
            adhoc.ExecuteAsync("CREATE TABLE z_collision (id INT)", cancellationToken: CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        };

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.ApplySchemaAsync(schema, CancellationToken.None));

        // Assert: prepared schema-owned work was discarded; existing objects survive.
        completed.ShouldBe(1);
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
        failure.InnerException!.InnerException.ShouldBeOfType<Catalog.SqlCatalogException>().Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03404");
        database.Catalog.TryGetTable("dbo", "a_first", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "z_collision", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "preserved", out var preserved).ShouldBeTrue();
        preserved.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        database.Catalog.SchemaState.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Review fix: touched-table SELECT and DDL wait and bind the published schema (#1778)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchemaAsync_ReviewTouchedStatement_ShouldWaitAndBindNewSchema(bool ddl)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collisions", guard.Token);
        var initial = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "collisions", false, [], [Table("orders")], []);
        await database.ApplySchemaAsync(initial, guard.Token);
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "collisions", false, [],
            [new CompiledSchemaTable("orders", "Row_orders",
                [new("id", DatabaseType.Int32, false), new("note", DatabaseType.String, true)],
                null, [new CompiledSchemaIndex("ix_orders_id", ["id"])], [])], []);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE INDEX ix_preserved ON orders(id)", cancellationToken: guard.Token);
        await session.ExecuteAsync("INSERT INTO orders VALUES(42)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = stage =>
        { if (stage == "Publishing") { entered.TrySetResult(); release.Wait(guard.Token); } };
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        var applying = Task.Run(async () => await database.ApplySchemaAsync(desired, guard.Token), guard.Token);
        await entered.Task.WaitAsync(guard.Token);
        try
        {
            // Act
            var statement = session.ExecuteAsync(ddl ? "CREATE INDEX ix_after ON orders(note)" : "SELECT * FROM orders", cancellationToken: guard.Token).AsTask();
            (await Task.WhenAny(waiting.Task, statement).WaitAsync(guard.Token)).ShouldBeSameAs(waiting.Task);
            statement.IsCompleted.ShouldBeFalse();
            release.Set(); await applying.WaitAsync(guard.Token);
            var result = await statement.WaitAsync(guard.Token);
            // Assert
            database.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
            table.FindColumn("note").ShouldNotBeNull();
            database.Catalog.TryGetIndex(table.ObjectId, "ix_preserved", out _).ShouldBeTrue();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(desired.Hash);
            if (ddl) { database.Catalog.TryGetIndex(table.ObjectId, "ix_after", out _).ShouldBeTrue(); }
            else
            {
                await using var rows = (Assimalign.Cohesion.Database.Execution.DatabaseResultSet)result;
                rows.Columns.Count.ShouldBe(2); rows.Columns[1].Name.ShouldBe("note");
                int count = 0;
                await foreach (var row in rows.GetRowsAsync(guard.Token))
                { count++; row.GetValue(0).ShouldBe(42); row.GetValue(1).ShouldBeNull(); }
                count.ShouldBe(1);
            }
        }
        finally { release.Set(); await applying.WaitAsync(guard.Token); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - B2: ad-hoc CREATE IF NOT EXISTS retains its ordinary no-op behavior")]
    public async Task ExecuteAsync_AdhocCreateIfNotExists_ShouldPreserveExistingObjects()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collisions", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE orders (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix_orders_id ON orders (id)", cancellationToken: CancellationToken.None);
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        await session.ExecuteAsync("CREATE TABLE IF NOT EXISTS orders (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX IF NOT EXISTS ix_orders_id ON orders (id)", cancellationToken: CancellationToken.None);

        // Assert
        database.Catalog.DdlGeneration.ShouldBe(generation);
        database.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        database.Catalog.TryGetIndex(table.ObjectId, "ix_orders_id", out _).ShouldBeTrue();
    }

    private static SqlDatabaseEngine CreateEngine()
    {
        var builder = SqlDatabaseEngine.CreateBuilder("provisioning-collisions");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        return builder.Build();
    }

    private static CompiledSchemaTable Table(string name)
        => new(name, "Row_" + name, [new CompiledSchemaColumn("id", DatabaseType.Int32, false)], null, [], []);
}
