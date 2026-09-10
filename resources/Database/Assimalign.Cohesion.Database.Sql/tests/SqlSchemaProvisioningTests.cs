using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlSchemaProvisioningTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "cohesion-sql-schema-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: records state and is idempotent across reopen")]
    public async Task ApplySchema_AcrossReopen_ShouldRecordAndShortCircuitSameHash()
    {
        // Arrange
        CompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);

        // Act / Assert: first application executes both ordered operations and records the document.
        await using (var engine = CreateEngine())
        {
            IDatabase database = await engine.CreateDatabaseAsync("orders");
            var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
            SchemaMigrationResult result = await provisioner.ApplySchemaAsync(schema);

            result.SourceHash.ShouldBeNull();
            result.TargetHash.ShouldBe(schema.Hash);
            result.AppliedOperationCount.ShouldBe(2);
            result.WasAlreadyApplied.ShouldBeFalse();

            var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
            instance.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(schema.Hash);
            instance.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
            instance.Catalog.TryGetIndex(table.ObjectId, "ix_orders_id", out _).ShouldBeTrue();
        }

        // Act / Assert: the durable catalog marker makes the identical schema a no-op after reopen.
        await using (var reopenedEngine = CreateEngine())
        {
            IDatabase reopened = await reopenedEngine.OpenDatabaseAsync("orders");
            var provisioner = reopened.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
            SchemaMigrationResult result = await provisioner.ApplySchemaAsync(schema);

            result.SourceHash.ShouldBe(schema.Hash);
            result.TargetHash.ShouldBe(schema.Hash);
            result.AppliedOperationCount.ShouldBe(0);
            result.WasAlreadyApplied.ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: persisted baseline drives additive migration")]
    public async Task ApplySchema_WithPersistedBaseline_ShouldApplyOnlyAdditions()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        CompiledSchema initial = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            []);
        await provisioner.ApplySchemaAsync(initial);

        CompiledSchema desired = OrdersSchema(
            [
                new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false),
                new CompiledSchemaColumn("note", DatabaseType.String, IsNullable: true, MaxLength: 100),
            ],
            [new CompiledSchemaIndex("ix_orders_note", ["note"])]);

        // Act
        SchemaMigrationResult result = await provisioner.ApplySchemaAsync(desired);

        // Assert
        result.SourceHash.ShouldBe(initial.Hash);
        result.TargetHash.ShouldBe(desired.Hash);
        result.AppliedOperationCount.ShouldBe(2);
        result.WasAlreadyApplied.ShouldBeFalse();

        var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
        instance.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        table.FindColumn("note").ShouldNotBeNull();
        instance.Catalog.TryGetIndex(table.ObjectId, "ix_orders_note", out _).ShouldBeTrue();
        instance.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: a failed later step compensates completed DDL")]
    public async Task ApplySchema_WhenLaterStepFails_ShouldRollbackCompletedStepsAndLeaveStateUnchanged()
    {
        // Arrange: the desired schema is valid, but its unique-index build fails against
        // duplicate live values after the preceding ADD COLUMN has completed.
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        CompiledSchema initial = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            [],
            hasPrimaryKey: false);
        await provisioner.ApplySchemaAsync(initial);
        await using (IDatabaseSession session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("INSERT INTO dbo.orders (id) VALUES (1), (1);");
        }

        CompiledSchema invalid = OrdersSchema(
            [
                new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false),
                new CompiledSchemaColumn("note", DatabaseType.String, IsNullable: true),
            ],
            [new CompiledSchemaIndex("ix_orders_id", ["id"], IsUnique: true)],
            hasPrimaryKey: false);

        // Act
        DatabaseSchemaMigrationException exception = await Should.ThrowAsync<DatabaseSchemaMigrationException>(
            async () => await provisioner.ApplySchemaAsync(invalid));

        // Assert: ADD COLUMN completed first, then its compensation removed it.
        exception.Message.ShouldContain("Every completed step was compensated");
        var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
        instance.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        table.FindColumn("note").ShouldBeNull();
        instance.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(initial.Hash);

        // A valid retry starts from the reconciled live catalog.
        CompiledSchema valid = OrdersSchema(
            [
                new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false),
                new CompiledSchemaColumn("note", DatabaseType.String, IsNullable: true),
            ],
            [],
            hasPrimaryKey: false);
        SchemaMigrationResult retry = await provisioner.ApplySchemaAsync(valid);
        retry.AppliedOperationCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: matching hash still reconciles live catalog drift")]
    public async Task ApplySchema_WhenRecordedHashMatchesButCatalogDrifts_ShouldReconcile()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        CompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);
        await provisioner.ApplySchemaAsync(schema);

        await using (IDatabaseSession session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("DROP INDEX ix_orders_id ON dbo.orders;");
        }

        // Act
        SchemaMigrationResult result = await provisioner.ApplySchemaAsync(schema);

        // Assert
        result.WasAlreadyApplied.ShouldBeFalse();
        result.AppliedOperationCount.ShouldBe(1);
        var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
        instance.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        instance.Catalog.TryGetIndex(table.ObjectId, "ix_orders_id", out _).ShouldBeTrue();
        instance.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(schema.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: matching hash repairs a malformed canonical document")]
    public async Task ApplySchema_WhenRecordedHashMatchesButDocumentIsMalformed_ShouldRepairState()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        CompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            []);
        await provisioner.ApplySchemaAsync(schema);

        var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
        await instance.Catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState(schema.Hash, "not-json"));

        // Act
        SchemaMigrationResult result = await provisioner.ApplySchemaAsync(schema);

        // Assert
        result.WasAlreadyApplied.ShouldBeFalse();
        result.AppliedOperationCount.ShouldBe(0);
        instance.Catalog.SchemaState.ShouldNotBeNull().CanonicalDocument.ShouldBe(
            CompiledSchemaSerializer.Serialize(schema));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: nullable primary keys remain idempotent")]
    public async Task ApplySchema_WithNullablePrimaryKey_ShouldApplyEffectiveNonNullability()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        CompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: true)],
            []);

        // Act
        SchemaMigrationResult first = await provisioner.ApplySchemaAsync(schema);
        SchemaMigrationResult second = await provisioner.ApplySchemaAsync(schema);

        // Assert
        first.AppliedOperationCount.ShouldBe(1);
        second.WasAlreadyApplied.ShouldBeTrue();
        second.AppliedOperationCount.ShouldBe(0);
        var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
        instance.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        table.FindColumn("id").ShouldNotBeNull().IsNullable.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: identifier casing converges without repeated DDL")]
    public async Task ApplySchema_WithIdentifierCasingChanged_ShouldConvergeAndBecomeIdempotent()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        CompiledSchema initial = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);
        CompiledSchema desired = OrdersSchema(
            [new CompiledSchemaColumn("ID", DatabaseType.Int64, IsNullable: false)],
            [new CompiledSchemaIndex("IX_ORDERS_ID", ["ID"])],
            tableName: "ORDERS");
        await provisioner.ApplySchemaAsync(initial);

        // Act
        SchemaMigrationResult casingUpdate = await provisioner.ApplySchemaAsync(desired);
        SchemaMigrationResult repeated = await provisioner.ApplySchemaAsync(desired);

        // Assert
        desired.Hash.ShouldNotBe(initial.Hash);
        casingUpdate.AppliedOperationCount.ShouldBe(0);
        casingUpdate.WasAlreadyApplied.ShouldBeFalse();
        repeated.AppliedOperationCount.ShouldBe(0);
        repeated.WasAlreadyApplied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: disposed databases reject provisioning")]
    public async Task ApplySchema_AfterDatabaseDisposal_ShouldThrow()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        CompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            []);
        await database.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(
            async () => await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema));
    }

    private SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "schema-tests",
            RootPath = _rootPath,
        });

    private static CompiledSchema OrdersSchema(
        CompiledSchemaColumn[] columns,
        CompiledSchemaIndex[] indexes,
        bool hasPrimaryKey = true,
        string tableName = "orders")
        => new(
            CompiledSchema.CurrentFormat,
            "orders",
            EngineModel.Sql,
            allowsDestructiveChanges: false,
            Array.Empty<CompiledSchemaType>(),
            [new CompiledSchemaTable(
                tableName,
                "Tests.Order",
                columns,
                hasPrimaryKey ? new CompiledSchemaKey($"pk_{tableName}", [columns[0].Name]) : null,
                indexes,
                Array.Empty<CompiledSchemaConstraint>())],
            Array.Empty<CompiledSchemaCollection>(),
            Array.Empty<CompiledSchemaFunction>(),
            Array.Empty<CompiledSchemaTrigger>(),
            Array.Empty<CompiledSchemaPrincipal>(),
            Array.Empty<CompiledSchemaExtension>());
}
