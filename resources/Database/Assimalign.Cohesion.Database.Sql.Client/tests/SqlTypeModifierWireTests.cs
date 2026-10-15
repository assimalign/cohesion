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

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlTypeModifierWireTests
{
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
                error.ConnectionUsable.ShouldBeTrue();
            }
        }

        // Assert
        await connection.ExecuteAsync("INSERT INTO added (id) VALUES (1)", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT id FROM added", cancellationToken: timeout.Token)).ShouldHaveSingleItem()["id"].ShouldBe(1);
    }
}
