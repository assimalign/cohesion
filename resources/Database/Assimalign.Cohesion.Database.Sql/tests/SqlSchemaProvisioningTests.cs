using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
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
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);

        // Act / Assert: first application executes both ordered operations and records the document.
        await using (var engine = CreateEngine())
        {
            IDatabase database = await engine.CreateDatabaseAsync("orders");
            var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
            SchemaMigrationResult result = await provisioner.ApplySchemaAsync(schema);

            result.FromHash.ShouldBeNull();
            result.ToHash.ShouldBe(schema.Hash);
            result.OperationCount.ShouldBe(2);
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

            result.FromHash.ShouldBe(schema.Hash);
            result.ToHash.ShouldBe(schema.Hash);
            result.OperationCount.ShouldBe(0);
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
        SqlCompiledSchema initial = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            []);
        await provisioner.ApplySchemaAsync(initial);

        SqlCompiledSchema desired = OrdersSchema(
            [
                new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false),
                new CompiledSchemaColumn("note", DatabaseType.String, IsNullable: true, MaxLength: 100),
            ],
            [new CompiledSchemaIndex("ix_orders_note", ["note"])]);

        // Act
        SchemaMigrationResult result = await provisioner.ApplySchemaAsync(desired);

        // Assert
        result.FromHash.ShouldBe(initial.Hash);
        result.ToHash.ShouldBe(desired.Hash);
        result.OperationCount.ShouldBe(2);
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
        SqlCompiledSchema initial = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            [],
            hasPrimaryKey: false);
        await provisioner.ApplySchemaAsync(initial);
        await using (IDatabaseSession session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("INSERT INTO dbo.orders (id) VALUES (1), (1);");
        }

        SqlCompiledSchema invalid = OrdersSchema(
            [
                new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false),
                new CompiledSchemaColumn("note", DatabaseType.String, IsNullable: true),
            ],
            [new CompiledSchemaIndex("ix_orders_id", ["id"], IsUnique: true)],
            hasPrimaryKey: false);

        // Act
        SqlSchemaMigrationException exception = await Should.ThrowAsync<SqlSchemaMigrationException>(
            async () => await provisioner.ApplySchemaAsync(invalid));

        // Assert: ADD COLUMN completed first, then its compensation removed it.
        exception.Message.ShouldContain("Every completed step was compensated");
        var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
        instance.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        table.FindColumn("note").ShouldBeNull();
        instance.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(initial.Hash);

        // A valid retry starts from the reconciled live catalog.
        SqlCompiledSchema valid = OrdersSchema(
            [
                new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false),
                new CompiledSchemaColumn("note", DatabaseType.String, IsNullable: true),
            ],
            [],
            hasPrimaryKey: false);
        SchemaMigrationResult retry = await provisioner.ApplySchemaAsync(valid);
        retry.OperationCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: matching hash still reconciles live catalog drift")]
    public async Task ApplySchema_WhenRecordedHashMatchesButCatalogDrifts_ShouldReconcile()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);
        await provisioner.ApplySchemaAsync(schema);

        // Simulate a partially completed previous schema application through its
        // internal session; live sessions cannot create this drift anymore.
        await using (IDatabaseSession session = database.ShouldBeOfType<SqlDatabaseInstance>()
            .CreateSchemaSession("orders", default))
        {
            await session.ExecuteAsync("DROP INDEX ix_orders_id ON dbo.orders;");
        }

        // Act
        SchemaMigrationResult result = await provisioner.ApplySchemaAsync(schema);

        // Assert
        result.WasAlreadyApplied.ShouldBeFalse();
        result.OperationCount.ShouldBe(1);
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
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            []);
        await provisioner.ApplySchemaAsync(schema);

        var instance = database.ShouldBeOfType<SqlDatabaseInstance>();
        await instance.Catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState(schema.Hash, "not-json"));

        // Act
        SchemaMigrationResult result = await provisioner.ApplySchemaAsync(schema);

        // Assert
        result.WasAlreadyApplied.ShouldBeFalse();
        result.OperationCount.ShouldBe(0);
        instance.Catalog.SchemaState.ShouldNotBeNull().CanonicalDocument.ShouldBe(
            SqlCompiledSchemaSerializer.Serialize(schema));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: nullable primary keys remain idempotent")]
    public async Task ApplySchema_WithNullablePrimaryKey_ShouldApplyEffectiveNonNullability()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: true)],
            []);

        // Act
        SchemaMigrationResult first = await provisioner.ApplySchemaAsync(schema);
        SchemaMigrationResult second = await provisioner.ApplySchemaAsync(schema);

        // Assert
        first.OperationCount.ShouldBe(1);
        second.WasAlreadyApplied.ShouldBeTrue();
        second.OperationCount.ShouldBe(0);
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
        SqlCompiledSchema initial = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);
        SqlCompiledSchema desired = OrdersSchema(
            [new CompiledSchemaColumn("ID", DatabaseType.Int64, IsNullable: false)],
            [new CompiledSchemaIndex("IX_ORDERS_ID", ["ID"])],
            tableName: "ORDERS");
        await provisioner.ApplySchemaAsync(initial);

        // Act
        SchemaMigrationResult casingUpdate = await provisioner.ApplySchemaAsync(desired);
        SchemaMigrationResult repeated = await provisioner.ApplySchemaAsync(desired);

        // Assert
        desired.Hash.ShouldNotBe(initial.Hash);
        casingUpdate.OperationCount.ShouldBe(0);
        casingUpdate.WasAlreadyApplied.ShouldBeFalse();
        repeated.OperationCount.ShouldBe(0);
        repeated.WasAlreadyApplied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema apply: disposed databases reject provisioning")]
    public async Task ApplySchema_AfterDatabaseDisposal_ShouldThrow()
    {
        // Arrange
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            []);
        await database.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(
            async () => await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ownership: session-created objects remain mutable")]
    public async Task SessionCreatedObjects_ShouldBeAdhocAndAllowDdl()
    {
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var catalog = database.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        await using IDatabaseSession session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE scratch (id BIGINT);");
        catalog.TryGetTable("dbo", "scratch", out var table).ShouldBeTrue();
        table.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        table.SchemaName.ShouldBeNull();
        await session.ExecuteAsync("ALTER TABLE scratch ADD COLUMN note TEXT;");
        await session.ExecuteAsync("CREATE INDEX ix_scratch_id ON scratch (id);");
        catalog.TryGetIndex(table.ObjectId, "ix_scratch_id", out var index).ShouldBeTrue();
        index.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        index.SchemaName.ShouldBeNull();
        await session.ExecuteAsync("DROP INDEX ix_scratch_id ON scratch;");
        await session.ExecuteAsync("ALTER TABLE scratch DROP COLUMN note;");
        await session.ExecuteAsync("DROP TABLE scratch;");

        catalog.TryGetTable("dbo", "scratch", out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ownership: schema objects reject session DDL")]
    [InlineData("DROP TABLE orders;", "orders", "DROP TABLE")]
    [InlineData("DROP TABLE IF EXISTS orders;", "orders", "DROP TABLE")]
    [InlineData("ALTER TABLE orders ADD COLUMN extra TEXT;", "orders", "ALTER TABLE ADD COLUMN")]
    [InlineData("ALTER TABLE orders DROP COLUMN note;", "orders", "ALTER TABLE DROP COLUMN")]
    [InlineData("DROP INDEX ix_orders_id ON orders;", "ix_orders_id", "DROP INDEX")]
    [InlineData("DROP INDEX IF EXISTS ix_orders_id ON orders;", "ix_orders_id", "DROP INDEX")]
    public async Task SchemaOwnedObjects_ShouldRejectSessionDdl(string statement, string objectName, string operation)
    {
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, false),
             new CompiledSchemaColumn("note", DatabaseType.String, true)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);
        await provisioner.ApplySchemaAsync(schema);
        await using IDatabaseSession session = await database.CreateSessionAsync();

        DatabaseObjectLockedException exception = await Should.ThrowAsync<DatabaseObjectLockedException>(
            async () => await session.ExecuteAsync(statement));

        exception.ObjectName.ShouldBe(objectName);
        exception.SchemaName.ShouldBe("orders");
        exception.Operation.ShouldBe(operation);
        exception.Message.ShouldContain(objectName);
        exception.Message.ShouldContain("orders");
        exception.Message.ShouldContain(operation);
        var catalog = database.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        table.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        table.SchemaName.ShouldBe("orders");
        table.FindColumn("note").ShouldNotBeNull();
        table.FindColumn("extra").ShouldBeNull();
        catalog.TryGetIndex(table.ObjectId, "ix_orders_id", out var index).ShouldBeTrue();
        index.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        index.SchemaName.ShouldBe("orders");
        (await provisioner.ApplySchemaAsync(schema)).WasAlreadyApplied.ShouldBeTrue();
        await session.ExecuteAsync("INSERT INTO orders (id, note) VALUES (1, 'mutable data');");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ownership: waiting DDL cannot mutate a schema-owned replacement")]
    [InlineData("DROP TABLE orders;", "orders", "DROP TABLE")]
    [InlineData("ALTER TABLE orders ADD COLUMN extra TEXT;", "orders", "ALTER TABLE ADD COLUMN")]
    [InlineData("ALTER TABLE orders DROP COLUMN note;", "orders", "ALTER TABLE DROP COLUMN")]
    [InlineData("DROP INDEX ix_orders_id ON orders;", "ix_orders_id", "DROP INDEX")]
    public async Task WaitingDdl_WhenTableReplacedBySchema_ShouldRecheckOwnership(
        string statement, string objectName, string operation)
    {
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var catalog = database.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        await using IDatabaseSession holder = await database.CreateSessionAsync();
        await using IDatabaseSession waiter = await database.CreateSessionAsync();
        await holder.ExecuteAsync("CREATE TABLE orders (id BIGINT PRIMARY KEY, note TEXT);");
        catalog.TryGetTable("dbo", "orders", out var original).ShouldBeTrue();
        await using IDatabaseTransaction holdingTransaction = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("CREATE INDEX ix_orders_id ON orders (id);");
        await using IDatabaseTransaction waitingTransaction = await waiter.BeginTransactionAsync();

        // The explicit transaction is already begun, so execution reaches the
        // holder's exclusive table lock synchronously before yielding here.
        var waiting = waiter.ExecuteAsync(statement, cancellationToken: TestTimeout.Token()).AsTask();
        waiting.IsCompleted.ShouldBeFalse();
        await holder.ExecuteAsync("DROP TABLE orders;");
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, false),
             new CompiledSchemaColumn("note", DatabaseType.String, true)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);
        await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema);
        await holdingTransaction.CommitAsync();

        DatabaseObjectLockedException exception = await Should.ThrowAsync<DatabaseObjectLockedException>(
            async () => await waiting);
        exception.ObjectName.ShouldBe(objectName);
        exception.SchemaName.ShouldBe("orders");
        exception.Operation.ShouldBe(operation);
        await waitingTransaction.RollbackAsync();
        catalog.TryGetTable("dbo", "orders", out var replacement).ShouldBeTrue();
        replacement.ObjectId.ShouldNotBe(original.ObjectId);
        replacement.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        replacement.FindColumn("note").ShouldNotBeNull();
        replacement.FindColumn("extra").ShouldBeNull();
        catalog.TryGetIndex(replacement.ObjectId, "ix_orders_id", out var index).ShouldBeTrue();
        index.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        (await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema))
            .WasAlreadyApplied.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ownership: waiting DDL cannot use a lock on a replaced identity")]
    [InlineData("DROP TABLE orders;")]
    [InlineData("ALTER TABLE orders ADD COLUMN extra TEXT;")]
    [InlineData("ALTER TABLE orders DROP COLUMN note;")]
    [InlineData("DROP INDEX ix_orders_id ON orders;")]
    public async Task WaitingDdl_WhenAdhocTableReplaced_ShouldRequireRetry(string statement)
    {
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var catalog = database.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        await using IDatabaseSession holder = await database.CreateSessionAsync();
        await using IDatabaseSession waiter = await database.CreateSessionAsync();
        await using IDatabaseSession creator = await database.CreateSessionAsync();
        await holder.ExecuteAsync("CREATE TABLE orders (id BIGINT PRIMARY KEY, note TEXT);");
        await using IDatabaseTransaction holdingTransaction = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync("CREATE INDEX ix_orders_id ON orders (id);");
        await using IDatabaseTransaction waitingTransaction = await waiter.BeginTransactionAsync();

        var waiting = waiter.ExecuteAsync(statement, cancellationToken: TestTimeout.Token()).AsTask();
        waiting.IsCompleted.ShouldBeFalse();
        await holder.ExecuteAsync("DROP TABLE orders;");
        await creator.ExecuteAsync("CREATE TABLE orders (id BIGINT PRIMARY KEY, note TEXT);");
        await creator.ExecuteAsync("CREATE INDEX ix_orders_id ON orders (id);");
        await holdingTransaction.CommitAsync();

        DatabaseException exception = await Should.ThrowAsync<DatabaseException>(async () => await waiting);
        exception.Message.ShouldContain("was replaced while waiting for a DDL lock");
        await waitingTransaction.RollbackAsync();
        catalog.TryGetTable("dbo", "orders", out var replacement).ShouldBeTrue();
        replacement.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        replacement.FindColumn("note").ShouldNotBeNull();
        replacement.FindColumn("extra").ShouldBeNull();
        catalog.TryGetIndex(replacement.ObjectId, "ix_orders_id", out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ownership: table and index locks survive engine restart")]
    public async Task SchemaOwnedObjects_AcrossRestart_ShouldRemainLocked()
    {
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, false)],
            [new CompiledSchemaIndex("ix_orders_id", ["id"])]);
        await using (var engine = CreateEngine())
        {
            IDatabase database = await engine.CreateDatabaseAsync("orders");
            await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema);
            await using IDatabaseSession session = await database.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE scratch (id BIGINT);");
        }

        await using var reopenedEngine = CreateEngine();
        IDatabase reopened = await reopenedEngine.OpenDatabaseAsync("orders");
        var catalog = reopened.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        table.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        table.SchemaName.ShouldBe("orders");
        catalog.TryGetIndex(table.ObjectId, "ix_orders_id", out var index).ShouldBeTrue();
        index.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        index.SchemaName.ShouldBe("orders");
        catalog.TryGetTable("dbo", "scratch", out var scratch).ShouldBeTrue();
        scratch.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        scratch.SchemaName.ShouldBeNull();
        await using IDatabaseSession reopenedSession = await reopened.CreateSessionAsync();
        await Should.ThrowAsync<DatabaseObjectLockedException>(
            async () => await reopenedSession.ExecuteAsync("DROP TABLE orders;"));
        await Should.ThrowAsync<DatabaseObjectLockedException>(
            async () => await reopenedSession.ExecuteAsync("DROP INDEX ix_orders_id ON orders;"));
        await reopenedSession.ExecuteAsync("DROP TABLE scratch;");
        (await reopened.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema))
            .WasAlreadyApplied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ownership: schema apply may remove owned columns indexes and tables")]
    public async Task SchemaApply_ShouldAuthorizeOwnedDdl()
    {
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        await provisioner.ApplySchemaAsync(OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, false),
             new CompiledSchemaColumn("note", DatabaseType.String, true)],
            [new CompiledSchemaIndex("ix_orders_note", ["note"])]));

        SqlCompiledSchema trimmed = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, false)], [],
            allowsDestructiveChanges: true);
        (await provisioner.ApplySchemaAsync(trimmed)).OperationCount.ShouldBe(2);
        var catalog = database.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
        table.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        table.SchemaName.ShouldBe("orders");
        table.FindColumn("note").ShouldBeNull();
        catalog.GetIndexes(table.ObjectId).ShouldBeEmpty();

        var empty = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", EngineModel.Sql,
            allowsDestructiveChanges: true, [], [], [], [], [], []);
        (await provisioner.ApplySchemaAsync(empty)).OperationCount.ShouldBe(1);
        catalog.Tables.ShouldBeEmpty();
        (await provisioner.ApplySchemaAsync(empty)).WasAlreadyApplied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ownership: schema apply preserves unrelated ad-hoc objects")]
    public async Task SchemaApply_ShouldPreserveUnrelatedAdhocObjects()
    {
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        await using IDatabaseSession session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE scratch (id BIGINT);");
        var provisioner = database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>();
        SqlCompiledSchema initial = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, false)], []);
        await provisioner.ApplySchemaAsync(initial);
        await session.ExecuteAsync("CREATE INDEX adhoc_index ON orders (id);");
        (await provisioner.ApplySchemaAsync(initial)).WasAlreadyApplied.ShouldBeTrue();

        var catalog = database.ShouldBeOfType<SqlDatabaseInstance>().Catalog;
        await catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState(initial.Hash, "invalid-json"));
        SqlCompiledSchema desired = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, false),
             new CompiledSchemaColumn("note", DatabaseType.String, true)], []);
        (await provisioner.ApplySchemaAsync(desired)).OperationCount.ShouldBe(1);
        catalog.TryGetTable("dbo", "scratch", out var scratch).ShouldBeTrue();
        scratch.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        await session.ExecuteAsync("DROP INDEX adhoc_index ON orders;");
        await session.ExecuteAsync("DROP TABLE scratch;");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ownership: schema apply does not adopt an ad-hoc table")]
    public async Task SchemaApply_WhenAdhocTableNameCollides_ShouldRefuseAdoption()
    {
        await using var engine = CreateEngine();
        IDatabase database = await engine.CreateDatabaseAsync("orders");
        await using IDatabaseSession session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE orders (id BIGINT PRIMARY KEY);");
        SqlCompiledSchema schema = OrdersSchema(
            [new CompiledSchemaColumn("id", DatabaseType.Int64, false)], []);

        await Should.ThrowAsync<SqlSchemaMigrationException>(
            async () => await database.ShouldBeAssignableTo<IDatabaseSchemaProvisioner>().ApplySchemaAsync(schema));

        database.ShouldBeOfType<SqlDatabaseInstance>().Catalog.SchemaState.ShouldBeNull();
        await session.ExecuteAsync("DROP TABLE orders;");
    }

    private SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "schema-tests",
            RootPath = _rootPath,
        });

    private static SqlCompiledSchema OrdersSchema(
        CompiledSchemaColumn[] columns,
        CompiledSchemaIndex[] indexes,
        bool hasPrimaryKey = true,
        string tableName = "orders",
        bool allowsDestructiveChanges = false)
        => new(
            SqlCompiledSchema.CurrentFormat,
            "orders",
            EngineModel.Sql,
            allowsDestructiveChanges,
            Array.Empty<CompiledSchemaType>(),
            [new CompiledSchemaTable(
                tableName,
                "Tests.Order",
                columns,
                hasPrimaryKey ? new CompiledSchemaKey($"pk_{tableName}", [columns[0].Name]) : null,
                indexes,
                Array.Empty<CompiledSchemaConstraint>())],
            Array.Empty<CompiledSchemaFunction>(),
            Array.Empty<CompiledSchemaTrigger>(),
            Array.Empty<CompiledSchemaPrincipal>(),
            Array.Empty<CompiledSchemaExtension>());
}
