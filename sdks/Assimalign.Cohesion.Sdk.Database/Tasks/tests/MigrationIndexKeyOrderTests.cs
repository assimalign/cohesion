using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Sdk.Database.Tasks;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public sealed class MigrationIndexKeyOrderTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Schema v12: migration defaults match live keys and repeat with zero operations (#1787)")]
    public async Task Execute_DefaultOrders_ShouldMatchDatabaseAndPlanNoRebuild()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [],
            [new("t", "Tests:Row", [new("id", DatabaseType.Int32, false), new("a", DatabaseType.Int32, true)],
                new("pk_t", ["id"]),
                [new("uq", ["a"], true), new("plain", ["a"]), new("zero", ["a"], KeyOrders: [default]),
                 new("ix_desc", ["a"], KeyOrders: [CompiledSchemaIndexKeyOrder.DescendingNullsLast]),
                 new("ix_last", ["a"], KeyOrders: [CompiledSchemaIndexKeyOrder.AscendingNullsLast]),
                 new("ix_first", ["a"], KeyOrders: [CompiledSchemaIndexKeyOrder.DescendingNullsFirst])], [])], []);
        Directory.CreateDirectory(directory.File("schemas"));
        SqlCompiledSchemaSerializer.Write(directory.File("schemas/orders.schema.json"), desired);
        File.WriteAllText(directory.File("schemas/schemas.manifest"), "orders\n");
        var build = new RecordingBuildEngine();
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = build, SchemaDirectory = directory.File("schemas"), DatabaseName = "orders",
            MigrationsRoot = directory.File("migrations"), MigrationName = "defaults", Model = "Sql", ProjectDirectory = directory.Path
        };
        await using var engine = SqlDatabaseEngine.Create("sdk-defaults", new());
        var database = await engine.CreateDatabaseAsync("orders", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, build.Errors.Select(error => error.Message)));
        string[] statements = File.ReadAllLines(task.MigrationPath).Where(line => !line.StartsWith("--", StringComparison.Ordinal)).ToArray();
        foreach (string statement in statements) { await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None); }

        // Assert: compare with runtime SQL, then rebuild the planner's index and
        // primary-key descriptions from public catalog metadata, not the baseline.
        statements.ShouldBe(SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(null, desired)).Steps.Select(step => step.StatementText));
        statements.ShouldContain(statement => statement.Contains("(a DESC)", StringComparison.Ordinal));
        statements.ShouldContain(statement => statement.Contains("(a NULLS LAST)", StringComparison.Ordinal));
        statements.ShouldContain(statement => statement.Contains("(a DESC NULLS FIRST)", StringComparison.Ordinal));
        var metadata = new System.Collections.Generic.List<object?[]>();
        await using (var result = (await session.ExecuteAsync(
            "SELECT INDEX_NAME,COLUMN_NAME,IS_UNIQUE,IS_PRIMARY_KEY,DIRECTION,NULL_PLACEMENT FROM INFORMATION_SCHEMA.INDEXES WHERE TABLE_NAME='t' ORDER BY INDEX_NAME,ORDINAL_POSITION",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<Assimalign.Cohesion.Database.Execution.DatabaseResultSet>())
        {
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { metadata.Add(Enumerable.Range(0, 6).Select(ordinal => row.GetValue(ordinal)).ToArray()); }
        }
        var primary = metadata.Where(row => Equals(row[3], "YES")).ToArray();
        primary.ShouldHaveSingleItem();
        primary.Select(Order).ShouldBe([CompiledSchemaIndexKeyOrder.AscendingNullsFirst]);
        var live = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [],
            [new("t", "Tests:Row", desired.Tables[0].Columns, new("pk_t", primary.Select(row => (string)row[1]!).ToArray()),
                metadata.Where(row => !Equals(row[3], "YES")).GroupBy(row => (string)row[0]!, StringComparer.Ordinal)
                    .Select(index => new CompiledSchemaIndex(index.Key, index.Select(row => (string)row[1]!).ToArray(),
                        Equals(index.First()[2], "YES"), KeyOrders: index.Select(Order).ToArray())).ToArray(), [])], []);
        SqlSchemaMigrationPlanner.Plan(live, desired).Operations.ShouldBeEmpty();
        SqlSchemaMigrationPlanner.Plan(SqlCompiledSchemaSerializer.Read(task.BaselinePath), desired).Operations.ShouldBeEmpty();
        task.MigrationName = "repeat";
        task.Execute().ShouldBeFalse();
        build.Errors.ShouldHaveSingleItem().Code.ShouldBe("COHDBSDK204");
        Directory.GetFiles(directory.File("migrations/orders"), "*.sql").ShouldHaveSingleItem();

        static CompiledSchemaIndexKeyOrder Order(object?[] row) => Equals(row[4], "DESC")
            ? Equals(row[5], "FIRST") ? CompiledSchemaIndexKeyOrder.DescendingNullsFirst : CompiledSchemaIndexKeyOrder.DescendingNullsLast
            : Equals(row[5], "LAST") ? CompiledSchemaIndexKeyOrder.AscendingNullsLast : CompiledSchemaIndexKeyOrder.AscendingNullsFirst;
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Directed migrations: creation and direction changes match runtime scripts (#1631)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_KeyOrderChange_ShouldRenderAndExecuteMatchingMigration(bool change)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var build = new RecordingBuildEngine();
        SqlCompiledSchema Schema(CompiledSchemaIndexKeyOrder order) => new(SqlCompiledSchema.CurrentFormat, "orders", true, [],
            [new("t", "Tests:Row", [new("a", DatabaseType.Int32, true), new("b", DatabaseType.Int32, true)], null,
                [new("ix", ["a", "b"], KeyOrders: [order, CompiledSchemaIndexKeyOrder.AscendingNullsFirst])], [])], []);
        var desired = Schema(CompiledSchemaIndexKeyOrder.DescendingNullsLast);
        var current = change ? Schema(CompiledSchemaIndexKeyOrder.DescendingNullsFirst) : null;
        Directory.CreateDirectory(directory.File("schemas"));
        SqlCompiledSchemaSerializer.Write(directory.File("schemas/orders.schema.json"), desired);
        File.WriteAllText(directory.File("schemas/schemas.manifest"), "orders\n");
        if (current is not null)
        {
            Directory.CreateDirectory(directory.File("migrations/orders"));
            SqlCompiledSchemaSerializer.Write(directory.File("migrations/orders/0001_initial.schema.json"), current);
        }
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = build, SchemaDirectory = directory.File("schemas"), DatabaseName = "orders",
            MigrationsRoot = directory.File("migrations"), MigrationName = "direction", Model = "Sql", ProjectDirectory = directory.Path
        };
        await using var engine = SqlDatabaseEngine.Create("sdk-directions", new());
        var database = await engine.CreateDatabaseAsync("orders", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        if (current is not null)
        {
            foreach (var step in SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(null, current)).Steps)
            { await session.ExecuteAsync(step.StatementText, cancellationToken: CancellationToken.None); }
        }

        // Act
        task.Execute().ShouldBeTrue(string.Join(Environment.NewLine, build.Errors.Select(error => error.Message)));
        string[] statements = File.ReadAllLines(task.MigrationPath).Where(line => !line.StartsWith("--", StringComparison.Ordinal)).ToArray();
        foreach (string statement in statements) { await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None); }

        // Assert
        statements.ShouldBe(SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(current, desired), current).Steps.Select(step => step.StatementText));
        statements.ShouldContain(statement => statement.Contains("a DESC, b", StringComparison.Ordinal));
        SqlCompiledSchemaSerializer.Read(task.BaselinePath).Hash.ShouldBe(desired.Hash);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,2),(2,1),(NULL,3)", cancellationToken: CancellationToken.None);
        (await ReadRowsAsync()).ShouldBe([2, 1]);

        async Task<int[]> ReadRowsAsync()
        {
            await using var result = (await session.ExecuteAsync("SELECT a FROM t WHERE a>0 ORDER BY a DESC", cancellationToken: CancellationToken.None))
                .ShouldBeAssignableTo<Assimalign.Cohesion.Database.Execution.DatabaseResultSet>();
            var values = new System.Collections.Generic.List<int>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add((int)row.GetValue(0)!); }
            return values.ToArray();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Directed migrations: v11 artifacts and baselines are refused (#1787)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_V11Document_ShouldRefuseBeforeWritingMigration(bool baseline)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", false, [], [], []);
        Directory.CreateDirectory(directory.File("schemas"));
        SqlCompiledSchemaSerializer.Write(directory.File("schemas/orders.schema.json"), schema);
        File.WriteAllText(directory.File("schemas/schemas.manifest"), "orders\n");
        string path = directory.File("schemas/orders.schema.json");
        if (baseline)
        {
            Directory.CreateDirectory(directory.File("migrations/orders"));
            path = directory.File("migrations/orders/0001_initial.schema.json");
        }
        string document = schema.CanonicalDocument.Replace("database-schema/v12", "database-schema/v11", StringComparison.Ordinal);
        File.WriteAllText(path, document);
        var build = new RecordingBuildEngine();
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = build, SchemaDirectory = directory.File("schemas"), DatabaseName = "orders",
            MigrationsRoot = directory.File("migrations"), MigrationName = "direction", Model = "Sql", ProjectDirectory = directory.Path
        };
        string[] entries = Directory.GetFileSystemEntries(directory.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        // Act / Assert
        task.Execute().ShouldBeFalse();
        build.Errors.ShouldContain(error => error.Code == "COHDBSDK206");
        File.ReadAllText(path).ShouldBe(document);
        Directory.GetFileSystemEntries(directory.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ShouldBe(entries);
        Directory.GetFiles(directory.Path, "*.sql", SearchOption.AllDirectories).ShouldBeEmpty();
    }
}
