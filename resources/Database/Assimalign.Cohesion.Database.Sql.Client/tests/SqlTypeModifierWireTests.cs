using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlTypeModifierWireTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Client] - Raw decimal metadata: omitted scale applies and reports zero over the wire")]
    public async Task QueryAsync_OmittedDecimalScale_ShouldReportAppliedMetadata()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var database = await harness.Engine.OpenDatabaseAsync("app", timeout.Token);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", false, [],
            [new CompiledSchemaTable("items", "Row", [new CompiledSchemaColumn("value", DatabaseType.Decimal, true, Precision: 3)], null, [], [])], []);

        // Act
        await database.ApplySchemaAsync(schema, timeout.Token);
        (await database.ApplySchemaAsync(schema, timeout.Token)).WasAlreadyApplied.ShouldBeTrue();
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("INSERT INTO items VALUES (12.4)", cancellationToken: timeout.Token);
        var metadata = await connection.QueryAsync("SELECT NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'items'",
            cancellationToken: timeout.Token);

        // Assert
        var row = metadata.ShouldHaveSingleItem();
        row["NUMERIC_PRECISION"].ShouldBe(3L);
        row["NUMERIC_SCALE"].ShouldBe(0L);
        (await connection.QueryAsync("SELECT value FROM items", cancellationToken: timeout.Token)).ShouldHaveSingleItem()["value"].ShouldBe(12m);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Temporal modifiers: every site reports unsupported and keeps the connection usable")]
    [MemberData(nameof(SqlTypeModifierCases.UnsupportedTemporal), MemberType = typeof(SqlTypeModifierCases))]
    public async Task ExecuteAsync_TemporalModifiers_ShouldPreserveCodeAndObject(string type, string member)
    {
        // Arrange: host declarations are refused before any listener starts.
        var builder = SqlDatabaseEngine.CreateBuilder("temporal-wire");
        var typedError = Should.Throw<SqlSchemaValidationException>(() => builder.AddDatabase("app", database =>
            database.Schema(schema => schema.Table<SqlTypeModifierCases.Row>("items", table => table.Column(SqlTypeModifierCases.Selector(member), type)))));
        typedError.Message.ShouldContain("COHDBL001", Case.Sensitive);
        typedError.Message.ShouldContain($"items.{member}", Case.Sensitive);
        var scriptBuilder = SqlDatabaseEngine.CreateBuilder("temporal-script-wire");
        scriptBuilder.AddDatabase("app", database => database.Script("001", $"CREATE TABLE scripted (value {type})"));
        var scriptError = await Should.ThrowAsync<DatabaseParseException>(async () => await scriptBuilder.BuildAsync(CancellationToken.None));
        scriptError.Message.ShouldContain("COHDBL001", Case.Sensitive);
        scriptError.Message.ShouldContain("Column 'value'", Case.Sensitive);
        scriptError.Message.ShouldContain(type, Case.Sensitive);
        await using var harness = await SqlClientTestHarness.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("CREATE TABLE added (id INT)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync($"CREATE TABLE altered (value {type.Split(' ', '(')[0]})", cancellationToken: timeout.Token);

        foreach (var (sql, subject) in SqlTypeModifierCases.Statements(type).Append(($"DROP FUNCTION parameter_function({type})", "parameter type")))
        {
            // Act / Assert
            var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: timeout.Token));
            error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
            error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
            error.Message.ShouldContain("COHDBL001", Case.Sensitive);
            error.Message.ShouldContain(subject, Case.Sensitive);
            error.Message.ShouldContain(type, Case.Sensitive);
            error.ConnectionUsable.ShouldBeTrue();
        }

        // Assert
        await connection.ExecuteAsync("INSERT INTO added VALUES (1)", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT id FROM added", cancellationToken: timeout.Token)).ShouldHaveSingleItem()["id"].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - FLOAT precision: catalog, casts, domains and function signatures carry the resolved type")]
    [MemberData(nameof(SqlTypeModifierCases.Floating), MemberType = typeof(SqlTypeModifierCases))]
    public async Task QueryAsync_FloatPrecision_ShouldReportResolvedTypes(string type, DatabaseType expected)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("CREATE TABLE added (id INT)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync($"CREATE TABLE altered (value {type})", cancellationToken: timeout.Token);

        // Act
        foreach (var (sql, _) in SqlTypeModifierCases.Statements(type))
        {
            await connection.ExecuteAsync(sql, cancellationToken: timeout.Token);
        }
        await connection.ExecuteAsync("INSERT INTO added (id) VALUES (1)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync($"CREATE FUNCTION identity_float(value {type}) RETURNS {type} RETURN value", cancellationToken: timeout.Token);
        var result = await connection.QueryAsync($"SELECT CAST(16777217 AS {type}) AS casted, identity_float(CAST(1 AS bounded)) AS called FROM added", cancellationToken: timeout.Token);

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([expected, expected]);
        var row = result.ShouldHaveSingleItem();
        if (expected == DatabaseType.Float32) { row["casted"].ShouldBeOfType<float>().ShouldBe(16777216f); }
        else { row["casted"].ShouldBeOfType<double>().ShouldBe(16777217d); }
        string resolved = expected == DatabaseType.Float32 ? "REAL" : "DOUBLE";
        foreach (string table in new[] { "created", "added", "altered" })
        {
            var metadata = await connection.QueryAsync($"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = 'value'", cancellationToken: timeout.Token);
            metadata.ShouldHaveSingleItem()["DATA_TYPE"].ShouldBe(expected == DatabaseType.Float32 ? "REAL" : "DOUBLE PRECISION");
        }
        (await connection.QueryAsync("SELECT BASE_TYPE FROM COHESION_SCHEMA.DOMAINS WHERE DOMAIN_NAME = 'bounded'", cancellationToken: timeout.Token))
            .ShouldHaveSingleItem()["BASE_TYPE"].ShouldBe(resolved);
        (await connection.QueryAsync("SELECT PARAMETER_TYPES FROM COHESION_SCHEMA.FUNCTIONS WHERE FUNCTION_NAME = 'parameter_function'", cancellationToken: timeout.Token))
            .ShouldHaveSingleItem()["PARAMETER_TYPES"].ShouldBe(resolved);
        (await connection.QueryAsync("SELECT RETURN_TYPE FROM COHESION_SCHEMA.FUNCTIONS WHERE FUNCTION_NAME = 'result_function'", cancellationToken: timeout.Token))
            .ShouldHaveSingleItem()["RETURN_TYPE"].ShouldBe(resolved);
        await connection.ExecuteAsync($"DROP FUNCTION parameter_function({type})", cancellationToken: timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Type modifiers: valid typed schemas and scripts serve clients after provisioning")]
    [MemberData(nameof(SqlTypeModifierCases.Columns), MemberType = typeof(SqlTypeModifierCases))]
    public async Task BuildAsync_HostDeclarationBoundary_ShouldValidateBeforeServingClients(string type, bool valid, string member)
    {
        // Arrange: host declarations have no wire verb; validate them before starting a listener.
        var builder = SqlDatabaseEngine.CreateBuilder("declared-wire");
        if (!valid)
        {
            // Act / Assert
            var typedError = Should.Throw<SqlSchemaValidationException>(() => builder.AddDatabase("typed", database =>
                database.Schema(schema => schema.Table<SqlTypeModifierCases.Row>("items", table =>
                    table.Column(SqlTypeModifierCases.Selector(member), type)))));
            typedError.Errors.ShouldHaveSingleItem().Code.ShouldBe(SqlSchemaValidationErrorCode.UnsupportedType);
            typedError.Message.ShouldContain($"items.{member}", Case.Sensitive);
            var scriptBuilder = SqlDatabaseEngine.CreateBuilder("invalid-script-wire");
            scriptBuilder.AddDatabase("app", database => database.Script("001", $"CREATE TABLE scripted (value {type})"));
            var scriptError = await Should.ThrowAsync<DatabaseParseException>(async () => await scriptBuilder.BuildAsync(CancellationToken.None));
            scriptError.Message.ShouldContain("SQL0005", Case.Sensitive);
            scriptError.Message.ShouldContain("Column 'value'", Case.Sensitive);
            return;
        }
        builder.AddDatabase("app", database =>
        {
            database.Schema(schema => schema.Table<SqlTypeModifierCases.Row>("items", table =>
                table.Column(SqlTypeModifierCases.Selector(member), type)));
            database.Script("001", $"CREATE TABLE added (id INT); CREATE TABLE altered (value {type}); " +
                string.Join("; ", SqlTypeModifierCases.Statements(type).Select(statement => statement.Sql)));
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = await builder.BuildAsync(timeout.Token);
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "app", Principal = "tester", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(timeout.Token);

        // Act / Assert: the typed column and scripted column, domain and functions resolve over the wire.
        (await connection.QueryAsync($"SELECT {member} FROM items", cancellationToken: timeout.Token)).ShouldBeEmpty();
        (await connection.QueryAsync("SELECT value FROM created", cancellationToken: timeout.Token)).ShouldBeEmpty();
        await connection.ExecuteAsync("INSERT INTO added (id) VALUES (1)", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT parameter_function(CAST(NULL AS bounded)) AS result FROM added", cancellationToken: timeout.Token))
            .ShouldHaveSingleItem()["result"].ShouldBe(1);
        (await connection.QueryAsync("SELECT result_function() AS result FROM added", cancellationToken: timeout.Token))
            .ShouldHaveSingleItem()["result"].ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Type modifiers: every declaration and CAST reports the same boundary over the wire")]
    [MemberData(nameof(SqlTypeModifierCases.Sql), MemberType = typeof(SqlTypeModifierCases))]
    public async Task ExecuteAsync_ModifierBoundary_ShouldPreserveCodeAndConnection(string type, bool valid, string storage)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        using var timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(30));
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("CREATE TABLE added (id INT)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync($"CREATE TABLE altered (value {(valid ? type : storage)})", cancellationToken: timeout.Token);

        foreach (var (sql, subject) in SqlTypeModifierCases.Statements(type))
        {
            // Act / Assert
            if (valid)
            {
                await connection.ExecuteAsync(sql, cancellationToken: timeout.Token);
            }
            else
            {
                var error = await Should.ThrowAsync<SqlClientException>(async () =>
                    await connection.ExecuteAsync(sql, cancellationToken: timeout.Token));
                error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
                error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
                error.Message.ShouldContain("SQL0005", Case.Sensitive);
                error.Message.ShouldContain(subject, Case.Sensitive);
                if (type == "FLOAT(0)") { error.Message.ShouldContain("at least 1 bit", Case.Sensitive); }
                if (type is "FLOAT(54)" or "FLOAT(2147483647)") { error.Message.ShouldContain("less than 54 bits", Case.Sensitive); }
                error.ConnectionUsable.ShouldBeTrue();
            }
        }

        // Assert
        await connection.ExecuteAsync("INSERT INTO added (id) VALUES (1)", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT id FROM added", cancellationToken: timeout.Token)).ShouldHaveSingleItem()["id"].ShouldBe(1);
    }
}
