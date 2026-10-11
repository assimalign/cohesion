using System;
using System.Collections.Generic;
using System.Globalization;
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

    [Theory(DisplayName = "Cohesion Test [Sql] - Build: DEFAULT conversion and effective type bounds fail before creating either file set")]
    [InlineData("Quantity", "INT", "'oops'")]
    [InlineData("Quantity", "INT", "2147483648")]
    [InlineData("Enabled", "BOOLEAN", "'oops'")]
    [InlineData("Code", "VARCHAR(3)", "'abcd'")]
    [InlineData("Code", "short_code", "'abcd'")]
    [InlineData("Amount", "NUMERIC(3, 2)", "'oops'")]
    [InlineData("Amount", "NUMERIC(3, 2)", "9.995")]
    [InlineData("Amount", "bounded_price", "9.995")]
    [InlineData("Amount", "NUMERIC(29, 0)", "79228162514264337593543950336")]
    [InlineData("Amount", "NUMERIC(30, 29)", "0.12345678901234567890123456789")]
    [InlineData("Amount", "NUMERIC(3, 2)", "'1e1'")]
    [InlineData("Ratio", "DOUBLE", "1e999")]
    [InlineData("Ratio", "DOUBLE", "1e-999")]
    [InlineData("Day", "DATE", "'not-a-date'")]
    [InlineData("Token", "UUID", "'not-a-guid'")]
    public async Task Build_WithUnstorableDefault_ShouldRefuseBeforeCreatingFiles(string column, string type, string literal)
    {
        // Arrange
        SqlSchema schema = SqlSchema.Create("defaults", declaration => declaration.Table<PreflightRow>("orders", table =>
        {
            switch (column)
            {
                case "Quantity":
                    table.Column(row => row.Quantity, type);
                    table.Default(row => row.Quantity, literal);
                    break;
                case "Enabled":
                    table.Column(row => row.Enabled, type);
                    table.Default(row => row.Enabled, literal);
                    break;
                case "Code":
                    table.Column(row => row.Code, type);
                    table.Default(row => row.Code, literal);
                    break;
                case "Amount":
                    table.Column(row => row.Amount, type);
                    table.Default(row => row.Amount, literal);
                    break;
                case "Ratio":
                    table.Column(row => row.Ratio, type);
                    table.Default(row => row.Ratio, literal);
                    break;
                case "Day":
                    table.Column(row => row.Day, type);
                    table.Default(row => row.Day, literal);
                    break;
                case "Token":
                    table.Column(row => row.Token, type);
                    table.Default(row => row.Token, literal);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(column));
            }
        }));
        var builder = SqlDatabaseEngine.CreateBuilder("default-preflight");
        builder.Options.RootPath = _rootPath;
        builder.Types.Add(SqlType.CreateDomain("short_code", SqlType.VarChar(3)));
        builder.Types.Add(SqlType.CreateDomain("bounded_price", SqlType.Decimal(3, 2)));
        builder.AddDatabase(schema);

        // Act
        SqlSchemaMigrationException refusal = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await builder.BuildAsync(CancellationToken.None));

        // Assert: neither the data nor catalog file set may exist after a preflight refusal.
        Directory.Exists(_rootPath).ShouldBeFalse(refusal.Message);
        refusal.Message.ShouldStartWith("COHSQLP001:", Case.Sensitive);
        refusal.Message.ShouldContain($"DEFAULT on column 'orders.{column}'", Case.Sensitive);
        refusal.Message.ShouldEndWith("Nothing was opened or written.", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Build: DEFAULT preflight preserves DDL rounding, character and domain conversion")]
    [InlineData(false, "'abc   '", "9.994", "abc", "9.99")]
    [InlineData(true, "'😀xy '", "'-9.994'", "😀xy", "-9.99")]
    [InlineData(false, "'a'", "0.123456789012345678901234567895", "a", "0.12")]
    [InlineData(true, "'abc'", "1.245e0", "abc", "1.25")]
    [InlineData(true, "'abc'", "'-1.245'", "abc", "-1.25")]
    public async Task Build_WithBoundedDefault_ShouldMatchDdlValues(bool useDomains, string codeLiteral,
        string amountLiteral, string expectedCode, string expectedAmount)
    {
        // Arrange
        string codeType = useDomains ? "short_code" : "VARCHAR(3)";
        string amountType = useDomains ? "bounded_price" : "NUMERIC(3, 2)";
        var builder = SqlDatabaseEngine.CreateBuilder("bounded-default-preflight");
        builder.Options.RootPath = _rootPath;
        builder.Types.Add(SqlType.CreateDomain("short_code", SqlType.VarChar(3)));
        builder.Types.Add(SqlType.CreateDomain("bounded_price", SqlType.Decimal(3, 2)));
        builder.AddDatabase(SqlSchema.Create("defaults", declaration => declaration.Table<BoundedDefaultRow>("orders", table =>
        {
            table.Column(row => row.Id);
            table.Column(row => row.Code, codeType);
            table.Default(row => row.Code, codeLiteral);
            table.Column(row => row.Amount, amountType);
            table.Default(row => row.Amount, amountLiteral);
        })));

        // Act
        await using SqlDatabaseEngine engine = await builder.BuildAsync(CancellationToken.None);
        SqlDatabase database = await engine.OpenDatabaseAsync("defaults", CancellationToken.None);
        await using SqlDatabaseSession session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE direct_defaults (Id INT NOT NULL, Code {codeType} NOT NULL DEFAULT {codeLiteral}, " +
            $"Amount {amountType} NOT NULL DEFAULT {amountLiteral})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO orders (Id) VALUES (1)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO direct_defaults (Id) VALUES (1)", cancellationToken: CancellationToken.None);

        // Assert
        var declaredRows = await RowsAsync(session, "SELECT Id, Code, Amount FROM orders", CancellationToken.None);
        declaredRows.ShouldBe(await RowsAsync(session, "SELECT Id, Code, Amount FROM direct_defaults", CancellationToken.None));
        declaredRows.ShouldBe([[1, expectedCode, decimal.Parse(expectedAmount, CultureInfo.InvariantCulture)]]);
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
    private sealed record PreflightRow(int Quantity, bool Enabled, string Code, decimal Amount, double Ratio, DateOnly Day, Guid Token);
    private sealed record BoundedDefaultRow(int Id, string Code, decimal Amount);
}
