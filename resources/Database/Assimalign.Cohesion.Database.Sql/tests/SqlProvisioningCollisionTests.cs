using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlProvisioningCollisionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - B2: table DDL between provisioning steps is refused before compensation")]
    public async Task ApplySchemaAsync_TableDdlBetweenSteps_ShouldRefuseAndCompensateOnlyItsOwnWork()
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
        database.SchemaProvisioner.StepApplied = step =>
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

        // Assert: only the completed schema-owned step was compensated.
        completed.ShouldBe(1);
        failure.Message.ShouldStartWith("CS03104:");
        failure.InnerException.ShouldBeOfType<DatabaseObjectLockedException>();
        database.Catalog.TryGetTable("dbo", "a_first", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "z_collision", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "preserved", out var preserved).ShouldBeTrue();
        preserved.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        database.Catalog.SchemaState.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - B2: index DDL between provisioning steps is refused and existing indexes survive compensation")]
    public async Task ApplySchemaAsync_IndexDdlBetweenSteps_ShouldRefuseAndPreserveExistingAdhocIndex()
    {
        // Arrange: keep the owning table from an earlier apply, so compensating
        // the added column must preserve an independently added index on id.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collisions", CancellationToken.None);
        var initial = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "collisions", false, [],
            [Table("orders")], []);
        await database.ApplySchemaAsync(initial, CancellationToken.None);
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "collisions", false, [],
            [new CompiledSchemaTable("orders", "Row_orders",
                [new("id", DatabaseType.Int32, false), new("note", DatabaseType.String, true)],
                null, [new CompiledSchemaIndex("ix_orders_id", ["id"])], [])], []);
        await using var adhoc = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await adhoc.ExecuteAsync("CREATE INDEX ix_preserved ON orders (id)", cancellationToken: CancellationToken.None);
        await adhoc.ExecuteAsync("INSERT INTO orders (id) VALUES (42)", cancellationToken: CancellationToken.None);
        int completed = 0;
        database.SchemaProvisioner.StepApplied = step =>
        {
            completed++;
            step.Operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AddColumn);
            adhoc.ExecuteAsync("CREATE INDEX ix_orders_id ON orders (id)", cancellationToken: CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        };

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.ApplySchemaAsync(desired, CancellationToken.None));

        // Assert
        completed.ShouldBe(1);
        failure.Message.ShouldStartWith("CS03104:");
        failure.InnerException.ShouldBeOfType<DatabaseObjectLockedException>();
        database.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        table.FindColumn("note").ShouldBeNull();
        database.Catalog.TryGetIndex(table.ObjectId, "ix_orders_id", out _).ShouldBeFalse();
        database.Catalog.TryGetIndex(table.ObjectId, "ix_preserved", out var preserved).ShouldBeTrue();
        preserved.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        database.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(initial.Hash);
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
