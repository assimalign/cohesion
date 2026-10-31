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
        statements.ShouldContain(statement => statement.Contains("a DESC NULLS LAST", StringComparison.Ordinal) && statement.Contains("b NULLS FIRST", StringComparison.Ordinal));
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

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Directed migrations: v10 artifacts and baselines are refused (#1631)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_V10Document_ShouldRefuseBeforeWritingMigration(bool baseline)
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
        string document = schema.CanonicalDocument.Replace("database-schema/v11", "database-schema/v10", StringComparison.Ordinal);
        File.WriteAllText(path, document);
        var build = new RecordingBuildEngine();
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = build, SchemaDirectory = directory.File("schemas"), DatabaseName = "orders",
            MigrationsRoot = directory.File("migrations"), MigrationName = "direction", Model = "Sql", ProjectDirectory = directory.Path
        };

        // Act / Assert
        task.Execute().ShouldBeFalse();
        build.Errors.ShouldContain(error => error.Code == "COHDBSDK206");
        File.ReadAllText(path).ShouldBe(document);
        Directory.GetFiles(directory.Path, "*.sql", SearchOption.AllDirectories).ShouldBeEmpty();
    }
}
