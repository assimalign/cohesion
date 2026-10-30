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

public sealed class MigrationPartialIndexTests
{
    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Partial migrations: predicate fragments cannot escape into statements or clauses (#1630)")]
    [InlineData("active; DROP TABLE t; --")]
    [InlineData("active ORDER BY k")]
    [InlineData("active --")]
    public void Execute_EscapingPredicate_ShouldRefuseWithoutScript(string predicate)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", true, [],
            [new("t", "Tests:Row", [new("k", DatabaseType.Int32, true), new("active", DatabaseType.Boolean, true)], null,
                [new("uq", ["k"], true, predicate)], [])], []);
        Directory.CreateDirectory(directory.File("schemas"));
        SqlCompiledSchemaSerializer.Write(directory.File("schemas/orders.schema.json"), desired);
        File.WriteAllText(directory.File("schemas/schemas.manifest"), "orders\n");
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = new RecordingBuildEngine(), SchemaDirectory = directory.File("schemas"), DatabaseName = "orders",
            MigrationsRoot = directory.File("migrations"), MigrationName = "escape", Model = "Sql", ProjectDirectory = directory.Path
        };

        // Act / Assert
        task.Execute().ShouldBeFalse();
        Directory.GetFiles(directory.File("migrations/orders")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sdk.Database] - Partial migrations: creation and predicate changes preserve subset and NULL uniqueness (#1630)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Execute_PartialUniqueIndex_ShouldMatchRuntimeAndPreserveSemantics(bool change, bool nulls)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var build = new RecordingBuildEngine();
        SqlCompiledSchema Schema(string predicate) => new(SqlCompiledSchema.CurrentFormat, "orders", true, [],
            [new("t", "Tests:Row", [new("k", DatabaseType.Int32, true), new("active", DatabaseType.Boolean, true)], null,
                [new("uq", ["k"], true, predicate)], [])], []);
        var desired = Schema(change ? "NOT active" : "active");
        var current = change ? Schema("active") : null;
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
            MigrationsRoot = directory.File("migrations"), MigrationName = "partial", Model = "Sql", ProjectDirectory = directory.Path
        };
        await using var engine = SqlDatabaseEngine.Create("sdk-partial", new());
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
        string values = nulls ? "(NULL,TRUE),(NULL,TRUE),(NULL,FALSE)" : "(7,TRUE),(7,FALSE)";
        await session.ExecuteAsync("INSERT INTO t VALUES" + values, cancellationToken: CancellationToken.None);

        // Assert
        statements.ShouldBe(SqlMigrationScriptGenerator.Generate(SqlSchemaMigrationPlanner.Plan(current, desired), current).Steps.Select(step => step.StatementText));
        SqlCompiledSchemaSerializer.Read(task.BaselinePath).Hash.ShouldBe(desired.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Partial migrations: predicate rendering safely escapes string literals (#1630)")]
    public void Execute_QuotedPredicate_ShouldRenderCanonicalSql()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "orders", true, [],
            [new("t", "Tests:Row", [new("k", DatabaseType.Int32, true), new("note", DatabaseType.String, true)], null,
                [new("uq", ["k"], true, "\"note\" = 'it''s; safe --'")], [])], []);
        Directory.CreateDirectory(directory.File("schemas"));
        SqlCompiledSchemaSerializer.Write(directory.File("schemas/orders.schema.json"), desired);
        File.WriteAllText(directory.File("schemas/schemas.manifest"), "orders\n");
        var task = new CreateDatabaseMigrationTask
        {
            BuildEngine = new RecordingBuildEngine(), SchemaDirectory = directory.File("schemas"), DatabaseName = "orders",
            MigrationsRoot = directory.File("migrations"), MigrationName = "quoted", Model = "Sql", ProjectDirectory = directory.Path
        };

        // Act / Assert
        task.Execute().ShouldBeTrue();
        File.ReadAllText(task.MigrationPath).ShouldContain("WHERE note = 'it''s; safe --';", Case.Sensitive);
    }
}
