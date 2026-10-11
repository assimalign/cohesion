using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Sdk.Database.Tasks;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public sealed class MigrationDefaultParityTests
{
    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Migration DEFAULT: new tables match runtime DDL and baseline")]
    [InlineData(DatabaseType.Int32, "7")]
    [InlineData(DatabaseType.Int32, "+7")]
    [InlineData(DatabaseType.Int32, "(7)")]
    [InlineData(DatabaseType.Decimal, "1.25")]
    [InlineData(DatabaseType.Decimal, "-1.25")]
    [InlineData(DatabaseType.Int32, "-7")]
    [InlineData(DatabaseType.String, "'it''s ready'")]
    [InlineData(DatabaseType.Boolean, "true")]
    [InlineData(DatabaseType.Int32, "null")]
    public void Execute_DefaultedTable_ShouldMatchRuntimeDdl(DatabaseType type, string literal)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        SqlCompiledSchema desired = Schema(type, literal);
        var buildEngine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, buildEngine, desired);
        SqlMigrationScript runtime = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(null, desired));

        // Act
        bool created = task.Execute();

        // Assert
        created.ShouldBeTrue(string.Join(Environment.NewLine, buildEngine.Errors.Select(static error => error.Message)));
        Statements(task).ShouldBe(runtime.Steps.Select(static step => step.StatementText));
        SqlCompiledSchemaSerializer.Read(task.BaselinePath).Hash.ShouldBe(desired.Hash);
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Migration DEFAULT: required additions match runtime DDL")]
    [InlineData(DatabaseType.Int32, "7")]
    [InlineData(DatabaseType.Int32, "+7")]
    [InlineData(DatabaseType.String, "'it''s ready'")]
    [InlineData(DatabaseType.Boolean, "false")]
    public void Execute_RequiredColumnWithDefault_ShouldMatchRuntimeDdl(DatabaseType type, string literal)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        SqlCompiledSchema current = Schema();
        SqlCompiledSchema desired = Schema(type, literal, allowDestructiveChanges: true);
        var buildEngine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, buildEngine, desired, current);
        SqlMigrationScript runtime = SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(current, desired), current);

        // Act
        bool created = task.Execute();

        // Assert
        created.ShouldBeTrue(string.Join(Environment.NewLine, buildEngine.Errors.Select(static error => error.Message)));
        Statements(task).ShouldBe(runtime.Steps.Select(static step => step.StatementText));
        SqlCompiledSchemaSerializer.Read(task.BaselinePath).Hash.ShouldBe(desired.Hash);
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Migration DEFAULT: required additions without a non-null default fail without a baseline")]
    [InlineData(null)]
    [InlineData("NULL")]
    [InlineData("(null)")]
    public void Execute_RequiredColumnWithoutValue_ShouldMatchRuntimeRefusal(string? literal)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        SqlCompiledSchema current = Schema();
        SqlCompiledSchema desired = Schema(DatabaseType.Int32, literal, allowDestructiveChanges: true);
        var buildEngine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, buildEngine, desired, current);

        // Act
        bool created = task.Execute();

        // Assert
        created.ShouldBeFalse();
        Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(current, desired), current));
        buildEngine.Errors.ShouldHaveSingleItem().Code.ShouldBe("COHDBSDK206");
        Directory.GetFiles(directory.File("migrations/orders")).Select(Path.GetFileName).ShouldBe(["0001_initial.schema.json"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Migration DEFAULT: nonliteral SQL fails without script or baseline")]
    [InlineData("1 + 2")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("'unterminated")]
    [InlineData("1); DROP TABLE Orders; --")]
    [InlineData("1 ORDER BY Id")]
    [InlineData("1; --")]
    [InlineData("-(1 + 2)")]
    [InlineData("-'text'")]
    public void Execute_NonliteralDefault_ShouldMatchRuntimeRefusal(string literal)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        SqlCompiledSchema desired = Schema(DatabaseType.Int32, literal);
        var buildEngine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask task = CreateTask(directory, buildEngine, desired);

        // Act
        bool created = task.Execute();

        // Assert
        created.ShouldBeFalse();
        Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(null, desired)));
        buildEngine.Errors.ShouldHaveSingleItem().Code.ShouldBe("COHDBSDK206");
        Directory.GetFiles(directory.File("migrations/orders")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Migration DEFAULT: SDK and runtime populate old and new rows identically")]
    public async Task Execute_RequiredDefaultedColumn_ShouldMatchRuntimeStoredValues()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        SqlCompiledSchema current = Schema();
        SqlCompiledSchema desired = Schema(DatabaseType.Int32, "7", allowDestructiveChanges: true);
        await using SqlDatabaseEngine sdkEngine = SqlDatabaseEngine.Create("sdk", new SqlDatabaseEngineOptions { RootPath = directory.File("sdk") });
        await using SqlDatabaseEngine runtimeEngine = SqlDatabaseEngine.Create("runtime", new SqlDatabaseEngineOptions { RootPath = directory.File("runtime") });
        SqlDatabase sdkDatabase = await sdkEngine.CreateDatabaseAsync("orders", cancellationToken: CancellationToken.None);
        SqlDatabase runtimeDatabase = await runtimeEngine.CreateDatabaseAsync("orders", cancellationToken: CancellationToken.None);
        await using SqlDatabaseSession sdkSession = await sdkDatabase.CreateSessionAsync(CancellationToken.None);
        await using SqlDatabaseSession runtimeSession = await runtimeDatabase.CreateSessionAsync(CancellationToken.None);
        var buildEngine = new RecordingBuildEngine();
        CreateDatabaseMigrationTask initial = CreateTask(directory, buildEngine, current);
        initial.Execute().ShouldBeTrue();
        foreach (string statement in Statements(initial))
        {
            await sdkSession.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
        }
        await runtimeDatabase.ApplySchemaAsync(current, CancellationToken.None);
        await sdkSession.ExecuteAsync("INSERT INTO Orders (Id) VALUES (42)", cancellationToken: CancellationToken.None);
        await runtimeSession.ExecuteAsync("INSERT INTO Orders (Id) VALUES (42)", cancellationToken: CancellationToken.None);
        CreateDatabaseMigrationTask migration = CreateTask(directory, buildEngine, desired);

        // Act
        migration.Execute().ShouldBeTrue(string.Join(Environment.NewLine, buildEngine.Errors.Select(static error => error.Message)));
        foreach (string statement in Statements(migration))
        {
            await sdkSession.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
        }
        await runtimeDatabase.ApplySchemaAsync(desired, CancellationToken.None);
        await sdkSession.ExecuteAsync("INSERT INTO Orders (Id) VALUES (43)", cancellationToken: CancellationToken.None);
        await runtimeSession.ExecuteAsync("INSERT INTO Orders (Id) VALUES (43)", cancellationToken: CancellationToken.None);

        // Assert
        (await QuantitiesAsync(sdkSession, CancellationToken.None)).ShouldBe([7, 7]);
        (await QuantitiesAsync(runtimeSession, CancellationToken.None)).ShouldBe([7, 7]);
    }

    private static async Task<List<int>> QuantitiesAsync(SqlDatabaseSession session, CancellationToken cancellationToken = default)
    {
        await using var result = (await session.ExecuteAsync("SELECT Quantity FROM Orders ORDER BY Id", cancellationToken: cancellationToken))
            .ShouldBeAssignableTo<QueryResultSet>();
        var values = new List<int>();
        await foreach (QueryRow row in result.GetRowsAsync(cancellationToken))
        {
            values.Add(row.GetValue(0).ShouldBeOfType<int>());
        }
        return values;
    }

    private static string[] Statements(CreateDatabaseMigrationTask task)
        => File.ReadAllLines(task.MigrationPath).Where(static line => !line.StartsWith("--", StringComparison.Ordinal)).ToArray();

    private static CreateDatabaseMigrationTask CreateTask(
        TemporaryDirectory directory,
        RecordingBuildEngine buildEngine,
        SqlCompiledSchema desired,
        SqlCompiledSchema? current = null)
    {
        Directory.CreateDirectory(directory.File("schemas"));
        SqlCompiledSchemaSerializer.Write(directory.File("schemas/orders.schema.json"), desired);
        File.WriteAllText(directory.File("schemas/schemas.manifest"), "orders\n");
        if (current is not null)
        {
            Directory.CreateDirectory(directory.File("migrations/orders"));
            SqlCompiledSchemaSerializer.Write(directory.File("migrations/orders/0001_initial.schema.json"), current);
        }

        return new CreateDatabaseMigrationTask
        {
            BuildEngine = buildEngine,
            SchemaDirectory = directory.File("schemas"),
            DatabaseName = "orders",
            MigrationsRoot = directory.File("migrations"),
            MigrationName = "defaults",
            Model = "Sql",
            ProjectDirectory = directory.Path
        };
    }

    private static SqlCompiledSchema Schema(DatabaseType? type = null, string? literal = null, bool allowDestructiveChanges = false)
    {
        CompiledSchemaColumn[] columns = type is null
            ? [new("Id", DatabaseType.Int64, false)]
            : [new("Id", DatabaseType.Int64, false), new("Quantity", type.Value, false, DefaultLiteral: literal)];
        return new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", allowDestructiveChanges, [],
            [new CompiledSchemaTable("Orders", "Tests:Order", columns, new CompiledSchemaKey("PK_Orders", ["Id"]), [], [])], []);
    }
}
