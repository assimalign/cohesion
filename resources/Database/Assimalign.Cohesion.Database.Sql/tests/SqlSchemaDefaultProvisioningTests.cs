using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
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
                table.DefaultSql(row => row.Quantity, "+5");
                table.Default(row => row.Note, "it's");
                table.Default(row => row.Enabled, true);
                table.Column(row => row.Code, "short_code");
                table.Default(row => row.Code, "abc");
                table.DefaultSql(row => row.Amount, "-1.25");
            });
        });
        SqlCompiledSchema compiled = schema.Compile();

        await using (SqlDatabaseEngine engine = await BuildAsync(schema, CancellationToken.None))
        {
            SqlDatabase database = await engine.OpenDatabaseAsync("defaults", CancellationToken.None);
            database.Catalog.TryGetTable("dbo", "orders", out var table).ShouldBeTrue();
            table.Columns.Select(static column => (column.Name, column.DefaultLiteral)).ShouldBe([
                ("Id", null), ("Quantity", "5"), ("Note", "'it''s'"), ("Enabled", "TRUE"), ("Code", "'abc'"), ("Amount", "-1.25")]);
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
                .ShouldBe([[5, "it's", true, "abc", -1.25m], [5, "it's", true, "abc", -1.25m]]);
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

    [Fact(DisplayName = "Cohesion Test [Sql] - Build: typed and SQL defaults populate omitted columns through the wire")]
    public async Task Build_WithTypedAndSqlDefaults_ShouldProvisionValuesOverWire()
    {
        // Arrange
        var day = new DateOnly(2026, 1, 1);
        var time = new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(1234567));
        var placed = day.ToDateTime(time);
        var offset = new DateTimeOffset(placed, TimeSpan.FromHours(-5));
        var duration = TimeSpan.FromDays(-2) - TimeSpan.FromTicks(1234567);
        var token = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        SqlSchema schema = SqlSchema.Create("defaults", declaration => declaration.Table<TypedDefaultRow>("orders", table =>
        {
            table.Key(row => row.Id);
            table.Default(row => row.Quantity, -7);
            table.Default(row => row.Amount, -1.2500m);
            table.Default(row => row.Active, true);
            table.Default(row => row.Status, "it's pending");
            table.Default(row => row.Code, '\'');
            table.Default(row => row.Day, day);
            table.Default(row => row.Time, time);
            table.Default(row => row.Placed, placed);
            table.Default(row => row.Offset, offset);
            table.Default(row => row.Duration, duration);
            table.Default(row => row.Token, token);
            table.Default(row => row.State, OrderState.Pending);
            table.Default(row => row.Payload, new byte[] { 0, 0x7f, 0x80, 0xff });
            table.Default(row => row.Note, (string?)null);
            table.DefaultSql(row => row.RawDay, "'2026-01-02'");
        }));
        await using SqlDatabaseEngine engine = await BuildAsync(schema, CancellationToken.None);
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(CancellationToken.None);
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "defaults",
                Principal = "typed-default-tests",
                EndPoint = listener.EndPoint,
            },
            ConnectionFactory = listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(CancellationToken.None);

        // Act
        await connection.ExecuteAsync("INSERT INTO orders (Id) VALUES (1)", cancellationToken: CancellationToken.None);
        var row = (await connection.QueryAsync("SELECT * FROM orders", cancellationToken: CancellationToken.None)).ShouldHaveSingleItem();

        // Assert
        row["Quantity"].ShouldBe(-7);
        row["Amount"].ShouldBe(-1.2500m);
        row["Active"].ShouldBe(true);
        row["Status"].ShouldBe("it's pending");
        row["Code"].ShouldBe("'");
        row["Day"].ShouldBe(day);
        row["Time"].ShouldBe(time);
        row["Placed"].ShouldBe(placed);
        row["Offset"].ShouldBe(offset);
        row["Duration"].ShouldBe(duration);
        row["Token"].ShouldBe(token);
        row["State"].ShouldBe((int)OrderState.Pending);
        row["Payload"].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 0, 0x7f, 0x80, 0xff });
        row["Note"].ShouldBeNull();
        row["RawDay"].ShouldBe(new DateOnly(2026, 1, 2));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DEFAULT: a materialized binary backfill cannot mutate the default for later rows")]
    public async Task ApplySchema_WithBinaryDefault_ShouldIsolateBackfillAndInsertedValues()
    {
        // Arrange
        SqlSchema initial = SqlSchema.Create("defaults", declaration => declaration.Table<BinaryDefaultRow>("orders", table => table.Key(row => row.Id)));
        await using SqlDatabaseEngine engine = SqlDatabaseEngine.Create("binary-defaults", new SqlDatabaseEngineOptions { RootPath = _rootPath });
        SqlDatabase database = await engine.CreateDatabaseAsync("defaults", cancellationToken: CancellationToken.None);
        await database.ApplySchemaAsync(initial.Compile(), CancellationToken.None);
        await using SqlDatabaseSession session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO orders (Id) VALUES (1)", cancellationToken: CancellationToken.None);
        byte[] declared = [0x01, 0x80, 0xff];
        SqlSchema desired = SqlSchema.Create("defaults", declaration =>
        {
            declaration.AllowDestructiveChanges();
            declaration.Table<BinaryDefaultRow>("orders", table =>
            {
                table.Key(row => row.Id);
                table.Default(row => row.Payload, declared);
            });
        });

        // Act: the old row gets its missing field directly from the bound default.
        await database.ApplySchemaAsync(desired.Compile(), CancellationToken.None);
        declared[0] = 0xee;
        var first = (await RowsAsync(session, "SELECT Payload FROM orders WHERE Id = 1", CancellationToken.None)).ShouldHaveSingleItem();
        first[0].ShouldBeOfType<byte[]>()[0] = 0xee;
        await session.ExecuteAsync("INSERT INTO orders (Id) VALUES (2)", cancellationToken: CancellationToken.None);

        // Assert
        var rows = await RowsAsync(session, "SELECT Payload FROM orders ORDER BY Id", CancellationToken.None);
        rows.Count.ShouldBe(2);
        foreach (object?[] row in rows)
        {
            row[0].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 0x01, 0x80, 0xff });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Build: binary SQL defaults reject malformed hex and length overflow before files exist")]
    [InlineData("'plain'")]
    [InlineData("'\\x0'")]
    [InlineData("'\\xGG'")]
    [InlineData("'\\x00 01'")]
    [InlineData("'\\x000102'")]
    public async Task Build_WithInvalidBinaryDefault_ShouldRefuseBeforeCreatingFiles(string literal)
    {
        // Arrange
        SqlSchema schema = SqlSchema.Create("defaults", declaration => declaration.Table<BinaryDefaultRow>("orders", table =>
        {
            table.Key(row => row.Id);
            table.Column(row => row.Payload, "BINARY(2)");
            table.DefaultSql(row => row.Payload, literal);
        }));

        // Act
        SqlSchemaMigrationException refusal = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await BuildAsync(schema, CancellationToken.None));

        // Assert
        refusal.Message.ShouldStartWith("COHSQLP001:", Case.Sensitive);
        refusal.Message.ShouldContain("DEFAULT on column 'orders.Payload'", Case.Sensitive);
        Directory.Exists(_rootPath).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Build: malformed and nonliteral DEFAULT declarations fail before creating files")]
    [InlineData("1 + 2")]
    [InlineData("Quantity")]
    [InlineData("@value")]
    [InlineData("'unterminated")]
    [InlineData("1); DROP TABLE orders; --")]
    [InlineData("1 FROM orders")]
    [InlineData("-(-1)")]
    [InlineData("-(1 + 2)")]
    [InlineData("DATE '2026-01-01'")]
    public async Task Build_WithMalformedDefault_ShouldRefuseBeforeCreatingFiles(string literal)
    {
        // Arrange
        SqlSchema schema = SqlSchema.Create("defaults", declaration => declaration.Table<DefaultRow>("orders", table =>
        {
            table.Key(row => row.Id);
            table.DefaultSql(row => row.Quantity, literal);
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
                    table.DefaultSql(row => row.Quantity, literal);
                    break;
                case "Enabled":
                    table.Column(row => row.Enabled, type);
                    table.DefaultSql(row => row.Enabled, literal);
                    break;
                case "Code":
                    table.Column(row => row.Code, type);
                    table.DefaultSql(row => row.Code, literal);
                    break;
                case "Amount":
                    table.Column(row => row.Amount, type);
                    table.DefaultSql(row => row.Amount, literal);
                    break;
                case "Ratio":
                    table.Column(row => row.Ratio, type);
                    table.DefaultSql(row => row.Ratio, literal);
                    break;
                case "Day":
                    table.Column(row => row.Day, type);
                    table.DefaultSql(row => row.Day, literal);
                    break;
                case "Token":
                    table.Column(row => row.Token, type);
                    table.DefaultSql(row => row.Token, literal);
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
            table.DefaultSql(row => row.Code, codeLiteral);
            table.Column(row => row.Amount, amountType);
            table.DefaultSql(row => row.Amount, amountLiteral);
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
            table.Default(row => row.Note, (string?)null);
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
    private sealed record BinaryDefaultRow(int Id, byte[] Payload);
    private sealed record TypedDefaultRow(int Id, int Quantity, decimal Amount, bool Active, string Status, char Code,
        DateOnly Day, TimeOnly Time, DateTime Placed, DateTimeOffset Offset, TimeSpan Duration, Guid Token,
        OrderState State, byte[] Payload, string? Note, DateOnly RawDay);
    private enum OrderState { Pending = 7 }
}
