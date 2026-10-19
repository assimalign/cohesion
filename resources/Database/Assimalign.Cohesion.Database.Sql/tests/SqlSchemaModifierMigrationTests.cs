using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlSchemaModifierMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Migration: code-first built-in bounds render validation-only ALTER")]
    [InlineData("VARCHAR(20)", "VARCHAR(10)", false, "VARCHAR(10)")]
    [InlineData("VARCHAR(10)", "VARCHAR(20)", false, "VARCHAR(20)")]
    [InlineData("DECIMAL(18,2)", "DECIMAL(10,2)", true, "DECIMAL(10, 2)")]
    [InlineData("DECIMAL(10,2)", "NUMERIC(18,2)", true, "NUMERIC(18, 2)")]
    public void Generate_CodeFirstModifierChange_ShouldRenderAlterColumn(string previousType, string desiredType,
        bool numeric, string expectedType)
    {
        // Arrange: use the public declaration/compiler path, which keeps modifiers in CustomType.
        var previous = Declare(previousType, numeric).Compile();
        var desired = Declare(desiredType, numeric).Compile();
        var plan = SqlSchemaMigrationPlanner.Plan(previous, desired);
        desired.Tables.Single().Columns.Single(column => column.Name == "Value").MaxLength.ShouldBeNull();
        desired.Tables.Single().Columns.Single(column => column.Name == "Value").Precision.ShouldBeNull();

        // Act
        var script = SqlMigrationScriptGenerator.Generate(plan, previous);

        // Assert
        var step = script.Steps.ShouldHaveSingleItem();
        step.Operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AlterColumn);
        step.StatementText.ShouldBe($"ALTER TABLE dbo.items ALTER COLUMN Value {expectedType} {(numeric ? "NOT NULL" : "NULL")};");
        step.RollbackRequest.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Migration: scale and domain rewrites remain refused")]
    [InlineData("DECIMAL(18,2)", "DECIMAL(18,3)", true)]
    [InlineData("email", "VARCHAR(10)", false)]
    [InlineData("email", "other_email", false)]
    public void Generate_CodeFirstRewrite_ShouldRefuseBeforeExecution(string previousType, string desiredType, bool numeric)
    {
        // Arrange
        var previous = Declare(previousType, numeric).Compile();
        var desired = Declare(desiredType, numeric).Compile();
        var plan = SqlSchemaMigrationPlanner.Plan(previous, desired);

        // Act / Assert
        Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(plan, previous))
            .Message.ShouldContain("storage-type, domain, scale or default rewrite");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Migration: an unchanged engine domain permits nullability tightening")]
    public void Generate_SameEngineDomain_ShouldRenderNullabilityChange()
    {
        // Arrange: domains are registered by the engine, outside the portable script generator.
        var previous = Declare("email", numeric: false).Compile();
        var table = previous.Tables.Single();
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, previous.Name,
            allowsDestructiveChanges: true, previous.Types,
            [new CompiledSchemaTable(table.Name, table.RowType,
                table.Columns.Select(column => column.Name == "Value" ? column with { IsNullable = false } : column).ToArray(),
                table.PrimaryKey, table.Indexes, table.Constraints)], previous.Principals);
        var plan = SqlSchemaMigrationPlanner.Plan(previous, desired);

        // Act
        var script = SqlMigrationScriptGenerator.Generate(plan, previous);

        // Assert
        script.Steps.ShouldHaveSingleItem().StatementText.ShouldBe("ALTER TABLE dbo.items ALTER COLUMN Value email NOT NULL;");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - 49a: code-first bound tightening validates populated rows on the worker")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_CodeFirstBoundTightening_ShouldDeferAndEnforceResolvedBounds(bool numeric)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new CrashCaptureSqlStorageStrategy();
        var previous = Declare(numeric ? "DECIMAL(18,2)" : "VARCHAR(20)", numeric);
        var desired = Declare(numeric ? "DECIMAL(10,2)" : "VARCHAR(10)", numeric);
        await using (var initial = await Builder(storage).AddDatabase(previous).BuildAsync(timeout.Token))
        {
            var database = await initial.OpenDatabaseAsync("modifiers", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync(numeric ? "INSERT INTO items VALUES (1, 12.34)" : "INSERT INTO items VALUES (1, 'fits')",
                cancellationToken: timeout.Token);
            storage = storage.CaptureDurableImages();
        }
        int applied = 0;
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database =>
            {
                database.SchemaProvisioner.BeforeDeferredStep = (_, token) =>
                {
                    entered.TrySetResult();
                    release.Wait(token);
                };
                database.SchemaProvisioner.StepApplied = _ => Interlocked.Increment(ref applied);
            };
            return new RecordingWorker(engine);
        });

        // Act
        await using var migrated = await builder.AddDatabase(desired).BuildAsync(timeout.Token);
        var result = await migrated.OpenDatabaseAsync("modifiers", timeout.Token);
        try
        {
            // Assert: Build returned with the old bound still published and the validation reserved.
            result.Provisioning.ShouldNotBeNull();
            await entered.Task.WaitAsync(timeout.Token);
            result.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
            var type = table.FindColumn("Value").ShouldNotBeNull().Type;
            if (numeric)
            {
                type.Precision.ShouldBe(18);
            }
            else
            {
                type.MaxLength.ShouldBe(20);
            }
            applied.ShouldBe(0);
        }
        finally
        {
            release.Set();
        }
        while (result.SchemaProvisioner.Deferred is not null)
        {
            await Task.Delay(1, timeout.Token);
        }
        result.OfflineError.ShouldBeNull();
        applied.ShouldBe(1);
        result.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(desired.Compile().Hash);
        await using var writer = await result.CreateSessionAsync(timeout.Token);
        await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync(
            numeric ? "INSERT INTO items VALUES (2, 123456789.00)" : "INSERT INTO items VALUES (2, 'elevenchars')",
            cancellationToken: timeout.Token));
    }

    private static SqlSchema Declare(string type, bool numeric) => SqlSchema.Create("modifiers", schema =>
    {
        schema.AllowDestructiveChanges();
        if (numeric)
        {
            schema.Table<NumericItem>("items", table =>
            {
                table.Key(row => row.Id);
                table.Column(row => row.Value, type);
            });
        }
        else
        {
            schema.Table<TextItem>("items", table =>
            {
                table.Key(row => row.Id);
                table.Column(row => row.Value, type);
            });
        }
    });

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy storage)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("modifiers");
        builder.Options.StorageStrategy = storage;
        return builder;
    }

    private sealed record TextItem(int Id, string Value);
    private sealed record NumericItem(int Id, decimal Value);
}
