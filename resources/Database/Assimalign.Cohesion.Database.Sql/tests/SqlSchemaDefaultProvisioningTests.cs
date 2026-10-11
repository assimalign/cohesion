using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlSchemaDefaultProvisioningTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-default-schema", Guid.NewGuid().ToString("N"));

    [Fact(DisplayName = "Cohesion Test [Sql] - Build: canonical DEFAULTs and domain bounds survive an out-of-band DDL replan")]
    public async Task Build_WithDefaultsAndOutOfBandDdl_ShouldPlanNoChangesAndPreserveValues()
    {
        // Arrange
        SqlSchema schema = SqlSchema.Create("defaults", declaration =>
        {
            declaration.Type<Price>(type => type.Decimal(8, 2));
            declaration.Table<DefaultRow>("orders", table =>
            {
                table.Key(row => row.Id);
                table.Default(row => row.Quantity, "+5");
                table.Default(row => row.Note, "'it''s'");
                table.Default(row => row.Enabled, "true");
                table.Column(row => row.Code, "short_code");
                table.Default(row => row.Code, "'abc'");
                table.Default(row => row.Amount, "1.25");
            });
        });
        SqlCompiledSchema compiled = schema.Compile();

        await using (SqlDatabaseEngine engine = await BuildAsync(schema, CancellationToken.None))
        {
            SqlDatabase database = await engine.OpenDatabaseAsync("defaults", CancellationToken.None);
            database.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
            table.Columns.Select(static column => (column.Name, column.DefaultLiteral)).ShouldBe([
                ("Id", null), ("Quantity", "5"), ("Note", "'it''s'"), ("Enabled", "TRUE"), ("Code", "'abc'"), ("Amount", "1.25")]);
            table.FindColumn("Code").ShouldNotBeNull().Type.MaxLength.ShouldBe(3);
            table.FindColumn("Amount").ShouldNotBeNull().Type.Precision.ShouldBe(8);
            table.FindColumn("Amount").ShouldNotBeNull().Type.Scale.ShouldBe(2);
            await using SqlDatabaseSession session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO orders (Id) VALUES (1)", cancellationToken: CancellationToken.None);
            var recorded = database.Catalog.SchemaState.ShouldNotBeNull();
            await session.ExecuteAsync("CREATE TABLE unrelated (id INT)", cancellationToken: CancellationToken.None);
            database.Catalog.DdlGeneration.ShouldBeGreaterThan(recorded.Generation);
        }

        // Act: the changed generation requires a snapshot of the persisted canonical literals.
        await using (SqlDatabaseEngine engine = await BuildAsync(schema, CancellationToken.None))
        {
            SqlDatabase database = await engine.OpenDatabaseAsync("defaults", CancellationToken.None);
            var result = engine.FindDeclaration(database.Name).ShouldNotBeNull().Result.ShouldNotBeNull();
            await using SqlDatabaseSession session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO orders (Id) VALUES (2)", cancellationToken: CancellationToken.None);

            // Assert: the original declaration spelling and effective domain types reconcile exactly.
            result.WasAlreadyApplied.ShouldBeFalse();
            result.OperationCount.ShouldBe(0);
            database.SchemaProvisioner.CatalogWalkCount.ShouldBeGreaterThan(0);
            database.SchemaProvisioner.LastPlan.ShouldNotBeNull().Operations.ShouldBeEmpty();
            var recorded = database.Catalog.SchemaState.ShouldNotBeNull();
            recorded.ContentHash.ShouldBe(compiled.Hash);
            recorded.Generation.ShouldBe(database.Catalog.DdlGeneration);
            recorded.CanonicalDocument.ShouldBe(compiled.CanonicalDocument);
            (await RowsAsync(session, "SELECT Quantity, Note, Enabled, Code, Amount FROM orders ORDER BY Id", CancellationToken.None))
                .ShouldBe([[5, "it's", true, "abc", 1.25m], [5, "it's", true, "abc", 1.25m]]);
        }

        // Assert: once the updated marker is durable, Build takes the catalog-free skip again.
        await using (SqlDatabaseEngine engine = await BuildAsync(schema, CancellationToken.None))
        {
            SqlDatabase database = await engine.OpenDatabaseAsync("defaults", CancellationToken.None);
            engine.FindDeclaration(database.Name).ShouldNotBeNull().Result.ShouldNotBeNull().WasAlreadyApplied.ShouldBeTrue();
            database.SchemaProvisioner.CatalogWalkCount.ShouldBe(0);
            database.SchemaProvisioner.LastPlan.ShouldBeNull();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Build: malformed and nonliteral DEFAULT declarations fail before creating files")]
    [InlineData("1 + 2")]
    [InlineData("Quantity")]
    [InlineData("@value")]
    [InlineData("'unterminated")]
    [InlineData("1); DROP TABLE orders; --")]
    [InlineData("1 FROM orders")]
    public async Task Build_WithMalformedDefault_ShouldRefuseBeforeCreatingFiles(string literal)
    {
        // Arrange
        SqlSchema schema = SqlSchema.Create("defaults", declaration => declaration.Table<DefaultRow>("orders", table =>
        {
            table.Key(row => row.Id);
            table.Default(row => row.Quantity, literal);
        }));

        // Act
        SqlSchemaMigrationException refusal = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await BuildAsync(schema, CancellationToken.None));

        // Assert
        refusal.Message.ShouldStartWith("COHSQLP001:", Case.Sensitive);
        refusal.Message.ShouldContain("DEFAULT on column 'orders.Quantity'", Case.Sensitive);
        refusal.Message.ShouldEndWith("Nothing was opened or written.", Case.Sensitive);
        Directory.Exists(_rootPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Build: DEFAULT NULL reconciles with the catalog's absent default")]
    public async Task Build_WithNullDefault_ShouldReconcileEffectiveAbsence()
    {
        // Arrange
        SqlSchema schema = SqlSchema.Create("defaults", declaration => declaration.Table<DefaultRow>("orders", table =>
        {
            table.Key(row => row.Id);
            table.Default(row => row.Note, "NULL");
        }));
        await using (SqlDatabaseEngine engine = await BuildAsync(schema, CancellationToken.None))
        {
            SqlDatabase database = await engine.OpenDatabaseAsync("defaults", CancellationToken.None);
            database.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
            table.FindColumn("Note").ShouldNotBeNull().DefaultLiteral.ShouldBeNull();
            await using SqlDatabaseSession session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO orders (Id) VALUES (1)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE unrelated (id INT)", cancellationToken: CancellationToken.None);
        }

        // Act
        await using SqlDatabaseEngine reopened = await BuildAsync(schema, CancellationToken.None);
        SqlDatabase current = await reopened.OpenDatabaseAsync("defaults", CancellationToken.None);
        var result = reopened.FindDeclaration(current.Name).ShouldNotBeNull().Result.ShouldNotBeNull();

        // Assert
        result.WasAlreadyApplied.ShouldBeFalse();
        result.OperationCount.ShouldBe(0);
        current.SchemaProvisioner.LastPlan.ShouldNotBeNull().Operations.ShouldBeEmpty();
        current.Catalog.SchemaState.ShouldNotBeNull().Generation.ShouldBe(current.Catalog.DdlGeneration);
        current.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(schema.Compile().Hash);
        await using SqlDatabaseSession query = await current.CreateSessionAsync(CancellationToken.None);
        (await RowsAsync(query, "SELECT Id, Note FROM orders", CancellationToken.None)).ShouldBe([[1L, null]]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private async Task<SqlDatabaseEngine> BuildAsync(SqlSchema schema, CancellationToken cancellationToken = default)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("schema-defaults");
        builder.Options.RootPath = _rootPath;
        builder.Types.Add(SqlType.CreateDomain("short_code", SqlType.VarChar(3)));
        builder.AddDatabase(schema);
        return await builder.BuildAsync(cancellationToken);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken = default)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: cancellationToken)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(cancellationToken))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }

        return rows;
    }

    private readonly record struct Price(decimal Value);
    private sealed record DefaultRow(long Id, int Quantity, string? Note, bool Enabled, string Code, Price Amount);
}
