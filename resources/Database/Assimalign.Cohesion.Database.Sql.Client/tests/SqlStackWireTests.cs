using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlStackWireTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Client] - TCP stack refusal reports CS05004 and preserves the exact session (#1151)")]
    public async Task QueryAsync_RuntimeStackFailure_ShouldKeepTcpSessionAsync()
    {
        // Arrange: exercise the runtime backstop over a real loopback connection.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("stack-wire");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("stack_refusal", _ =>
        {
            calls++;
            throw new InsufficientExecutionStackException();
        }, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(guard.Token);
        await engine.CreateDatabaseAsync("stack", guard.Token);
        await using var listener = new TcpConnectionListener(new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(guard.Token);
        await using var client = SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "stack", Principal = "tester", EndPoint = listener.EndPoint },
            ConnectionFactory = new TcpConnectionFactory(),
        });
        await using var connection = await client.ConnectAsync(guard.Token);
        Guid session = server.Sessions.ShouldHaveSingleItem().Id;

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("SELECT stack_refusal(1)", cancellationToken: guard.Token));

        // Assert
        calls.ShouldBe(1);
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05004");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT CAST(7 AS BIGINT)", cancellationToken: guard.Token))
            .ShouldHaveSingleItem()[0].ShouldBe(7L);
        server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
