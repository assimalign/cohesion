using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Sdk.Database.Tasks;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public sealed class MigrationAlterColumnParityTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - ALTER COLUMN: every golden shape matches per-step engine lowering and refusals")]
    public void Execute_AlterColumnCorpus_ShouldMatchEngine()
    {
        // Arrange
        foreach (var item in MigrationAlterColumnCases.All())
        {
            using var directory = new TemporaryDirectory();
            var current = MigrationAlterColumnCases.Schema(item.Previous);
            var desired = MigrationAlterColumnCases.Schema(item.Desired);
            var engine = new RecordingBuildEngine();
            var task = CreateTask(directory, engine, current, desired);
            var plan = SqlSchemaMigrationPlanner.Plan(current, desired);
            SqlMigrationScript? expected = null;
            DatabaseException? refusal = null;
            try { expected = SqlMigrationScriptGenerator.Generate(plan, current); }
            catch (DatabaseException failure) { refusal = failure; }

            // Act
            bool created = task.Execute();

            // Assert
            if (refusal is not null)
            {
                created.ShouldBeFalse(item.Name);
                var error = engine.Errors.ShouldHaveSingleItem();
                error.Code.ShouldBe("COHDBSDK206", item.Name);
                error.Message.ShouldBe(refusal.Message, item.Name);
            }
            else if (plan.IsEmpty)
            {
                created.ShouldBeFalse(item.Name);
                engine.Errors.ShouldHaveSingleItem().Code.ShouldBe("COHDBSDK204", item.Name);
            }
            else
            {
                created.ShouldBeTrue(item.Name + ": " + string.Join("; ", engine.Errors.Select(error => error.Message)));
                Statements(task).ShouldBe(expected!.Steps.Select(step => step.StatementText), item.Name);
                File.ReadAllText(task.MigrationPath).ShouldContain(
                    "-- Typed schema application publishes this migration jointly; this file applies per statement.\n", Case.Sensitive);
                SqlCompiledSchemaSerializer.Read(task.BaselinePath).Hash.ShouldBe(desired.Hash, item.Name);
            }
            if (!created)
            {
                Directory.GetFiles(directory.File("migrations/orders")).Select(Path.GetFileName).ShouldBe(["0001_initial.schema.json"], item.Name);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - ALTER COLUMN: default-only changes keep the planner's destructive opt-in")]
    [InlineData(null, "1")]
    [InlineData("1", null)]
    public void Execute_DefaultChangeWithoutOptIn_ShouldMatchPlannerRefusal(string? previous, string? desired)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var current = MigrationAlterColumnCases.Schema(new("Value", DatabaseType.Int32, true, DefaultLiteral: previous));
        var target = MigrationAlterColumnCases.Schema(new("Value", DatabaseType.Int32, true, DefaultLiteral: desired), destructive: false);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, engine, current, target);
        var refusal = Should.Throw<SqlSchemaMigrationException>(() => SqlSchemaMigrationPlanner.Plan(current, target));

        // Act
        bool created = task.Execute();

        // Assert
        created.ShouldBeFalse();
        engine.Errors.ShouldHaveSingleItem().Message.ShouldBe(refusal.Message);
        Directory.GetFiles(directory.File("migrations/orders")).Select(Path.GetFileName).ShouldBe(["0001_initial.schema.json"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - ALTER TABLE: metadata changes retain the engine per-step refusal")]
    public void Execute_ReorderedColumns_ShouldMatchEngineRefusal()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var current = MigrationAlterColumnCases.Schema(new("Value", DatabaseType.Int32, true));
        var table = current.Tables[0];
        var desired = new SqlCompiledSchema(current.Format, current.Name, true, [],
            [new(table.Name, table.RowType, table.Columns.Reverse().ToArray(), table.PrimaryKey, [], [])], []);
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, engine, current, desired);
        var refusal = Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(
            SqlSchemaMigrationPlanner.Plan(current, desired), current));

        // Act
        bool created = task.Execute();

        // Assert
        created.ShouldBeFalse();
        engine.Errors.ShouldHaveSingleItem().Code.ShouldBe("COHDBSDK206");
        engine.Errors[0].Message.ShouldBe(refusal.Message);
        Directory.GetFiles(directory.File("migrations/orders")).Select(Path.GetFileName).ShouldBe(["0001_initial.schema.json"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - ALTER DEFAULT: invalid literals preserve the engine's precise refusal reason")]
    [InlineData("1 + 2")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("'unterminated")]
    [InlineData("1); DROP TABLE Orders; --")]
    [InlineData("1 ORDER BY Id")]
    [InlineData("1; --")]
    [InlineData("-(1 + 2)")]
    [InlineData("-'text'")]
    [InlineData("nextval('s')")]
    [InlineData("1 + nextval('s')")]
    [InlineData("CAST('s' AS REGCLASS)")]
    [InlineData("NEXT VALUE FOR s")]
    [InlineData("(SELECT nextval('s') FROM Orders)")]
    public void Execute_InvalidAlterDefault_ShouldRetainEngineReason(string literal)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var current = MigrationAlterColumnCases.Schema(new("Value", DatabaseType.Int32, true));
        var desired = MigrationAlterColumnCases.Schema(new("Value", DatabaseType.Int32, true, DefaultLiteral: literal));
        var engine = new RecordingBuildEngine();
        var task = CreateTask(directory, engine, current, desired);
        var refusal = Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(
            SqlSchemaMigrationPlanner.Plan(current, desired), current));

        // Act
        bool created = task.Execute();

        // Assert
        created.ShouldBeFalse();
        engine.Errors.ShouldHaveSingleItem().Code.ShouldBe("COHDBSDK206");
        engine.Errors[0].Message.ShouldBe(refusal.Message);
        Directory.GetFiles(directory.File("migrations/orders")).Select(Path.GetFileName).ShouldBe(["0001_initial.schema.json"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - ALTER DEFAULT: grouped declarations retain declaration and guard limits")]
    [InlineData(0, "1", true)]
    [InlineData(1, "1", false)]
    [InlineData(0, "nextval('s')", false)]
    public void Execute_NestingBoundary_ShouldMatchEngine(int offset, string expression, bool supported)
    {
        // Arrange: use the same explicit stack for engine and task parsing on every platform.
        int depth = Assimalign.Cohesion.Database.Sql.Language.SqlQueryParserOptions.DefaultExpressionNestingLimit + offset;
        string literal = new string('(', depth) + expression + new string(')', depth);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var directory = new TemporaryDirectory();
                var current = MigrationAlterColumnCases.Schema(new("Value", DatabaseType.Int32, true));
                var desired = MigrationAlterColumnCases.Schema(new("Value", DatabaseType.Int32, true, DefaultLiteral: literal));
                var engine = new RecordingBuildEngine();
                var task = CreateTask(directory, engine, current, desired);
                var plan = SqlSchemaMigrationPlanner.Plan(current, desired);

                // Act / Assert
                if (supported)
                {
                    var expected = SqlMigrationScriptGenerator.Generate(plan, current);
                    task.Execute().ShouldBeTrue();
                    Statements(task).ShouldBe(expected.Steps.Select(step => step.StatementText));
                    Statements(task).ShouldBe(["ALTER TABLE dbo.Orders ALTER COLUMN Value SET DEFAULT 1;"]);
                }
                else
                {
                    var refusal = Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(plan, current));
                    task.Execute().ShouldBeFalse();
                    engine.Errors.ShouldHaveSingleItem().Message.ShouldBe(refusal.Message);
                    Directory.GetFiles(directory.File("migrations/orders")).Select(Path.GetFileName).ShouldBe(["0001_initial.schema.json"]);
                }
            }
            catch (Exception exception) { failure = exception; }
        }, 16 * 1024 * 1024) { IsBackground = true };

        // Act
        thread.Start();

        // Assert
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        failure.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - ALTER COLUMN: empty/populated files round-trip against inline/worker typed catalogs")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_TypeDefaultAndNullabilityChange_ShouldMatchTypedCatalog(bool populated, bool inline)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        SqlSchema previous = Declaration(wide: false);
        SqlSchema desired = Declaration(wide: true);
        await using (var initial = await Builder(directory.File("typed"), previous, inline).BuildAsync(timeout.Token))
        {
            if (populated)
            {
                var database = await initial.OpenDatabaseAsync("orders", timeout.Token);
                await using var session = await database.CreateSessionAsync(timeout.Token);
                await session.ExecuteAsync("INSERT INTO Orders (Id) VALUES (1)", cancellationToken: timeout.Token);
            }
        }
        await using var sdkEngine = SqlDatabaseEngine.Create("sdk", new SqlDatabaseEngineOptions { RootPath = directory.File("sdk") });
        var sdkDatabase = await sdkEngine.CreateDatabaseAsync("orders", cancellationToken: timeout.Token);
        await using var sdkSession = await sdkDatabase.CreateSessionAsync(timeout.Token);
        var log = new RecordingBuildEngine();
        var initialTask = CreateTask(directory, log, null, previous.Compile());
        initialTask.Execute().ShouldBeTrue();
        foreach (string statement in Statements(initialTask))
        {
            await sdkSession.ExecuteAsync(statement, cancellationToken: timeout.Token);
        }
        if (populated)
        {
            await sdkSession.ExecuteAsync("INSERT INTO Orders (Id) VALUES (1)", cancellationToken: timeout.Token);
        }
        var migration = CreateTask(directory, log, null, desired.Compile());

        // Act
        migration.Execute().ShouldBeTrue(string.Join("; ", log.Errors.Select(error => error.Message)));
        foreach (string statement in Statements(migration))
        {
            await sdkSession.ExecuteAsync(statement, cancellationToken: timeout.Token);
        }
        await using var typedEngine = await Builder(directory.File("typed"), desired, inline).BuildAsync(timeout.Token);
        var typedDatabase = await typedEngine.OpenDatabaseAsync("orders", timeout.Token);
        await using var typedSession = await typedDatabase.CreateSessionAsync(timeout.Token);
        // Session admission waits for the typed worker's recorded publication, without a timing assertion.
        await typedSession.ExecuteAsync("INSERT INTO Orders (Id) VALUES (2)", cancellationToken: timeout.Token);
        await sdkSession.ExecuteAsync("INSERT INTO Orders (Id) VALUES (2)", cancellationToken: timeout.Token);

        // Assert: compare the public catalog, then stored values and new DEFAULT/NOT NULL enforcement.
        const string catalog = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE " +
            "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'Orders' ORDER BY ORDINAL_POSITION";
        (await RowsAsync(sdkSession, catalog, timeout.Token)).ShouldBe(await RowsAsync(typedSession, catalog, timeout.Token));
        (await RowsAsync(sdkSession, "SELECT Id, Value FROM Orders ORDER BY Id", timeout.Token)).ShouldBe(
            populated ? ["1|1", "2|2"] : ["2|2"]);
        (await RowsAsync(typedSession, "SELECT Id, Value FROM Orders ORDER BY Id", timeout.Token)).ShouldBe(
            await RowsAsync(sdkSession, "SELECT Id, Value FROM Orders ORDER BY Id", timeout.Token));
        await Should.ThrowAsync<DatabaseException>(async () => await sdkSession.ExecuteAsync(
            "INSERT INTO Orders (Id, Value) VALUES (3, NULL)", cancellationToken: timeout.Token));
        await Should.ThrowAsync<DatabaseException>(async () => await typedSession.ExecuteAsync(
            "INSERT INTO Orders (Id, Value) VALUES (3, NULL)", cancellationToken: timeout.Token));
        typedDatabase.Provisioning?.FailureCode.ShouldBeNull();
    }

    private static SqlDatabaseEngineBuilder Builder(string root, SqlSchema schema, bool inline)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("typed");
        builder.Options.RootPath = root;
        builder.AddDatabase("orders", database =>
        {
            database.Schema(schema);
            database.RunMigrationsInline = inline;
        });
        return builder;
    }

    private static SqlSchema Declaration(bool wide) => SqlSchema.Create("orders", schema =>
    {
        schema.AllowDestructiveChanges();
        if (wide)
        {
            schema.Table<MigrationWideRow>("Orders", table =>
            {
                table.Key(row => row.Id);
                table.Default(row => row.Value, 2L);
            });
        }
        else
        {
            schema.Table<MigrationNarrowRow>("Orders", table =>
            {
                table.Key(row => row.Id);
                table.Default(row => row.Value, (int?)1);
            });
        }
    });

    private static async Task<string[]> RowsAsync(SqlDatabaseSession session, string query, CancellationToken cancellationToken = default)
    {
        await using var result = (await session.ExecuteAsync(query, cancellationToken: cancellationToken)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(cancellationToken))
        {
            rows.Add(string.Join("|", Enumerable.Range(0, row.FieldCount).Select(index => Convert.ToString(row.GetValue(index), CultureInfo.InvariantCulture) ?? "<null>")));
        }
        return rows.ToArray();
    }

    private static string[] Statements(CreateDatabaseMigrationTask task)
        => File.ReadAllLines(task.MigrationPath).Where(line => !line.StartsWith("--", StringComparison.Ordinal)).ToArray();

    private static CreateDatabaseMigrationTask CreateTask(TemporaryDirectory directory, RecordingBuildEngine engine,
        SqlCompiledSchema? current, SqlCompiledSchema desired)
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
            BuildEngine = engine, SchemaDirectory = directory.File("schemas"), DatabaseName = "orders",
            MigrationsRoot = directory.File("migrations"), MigrationName = "alter-column", Model = "Sql", ProjectDirectory = directory.Path,
        };
    }
}
