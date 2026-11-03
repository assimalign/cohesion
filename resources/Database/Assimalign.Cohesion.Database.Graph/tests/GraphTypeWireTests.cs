using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphTypeWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Graph types: node enforcement crosses InMemory and TCP without breaking the session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_NodeTypeViolation_ShouldDeliverDiagnosticAndRemainReadyAsync(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("types-wire", new());
        var database = await engine.CreateDatabaseAsync("graph", token);
        await using var session = await database.CreateSessionAsync(token);
        var schema = GraphSchema.Open(database, session);
        await schema.SetGraphTypeAsync(GraphType.Create([GraphNodeType.Create("N", ["I"],
            [GraphPropertyType.Create("p", DatabaseType.Int64, true)])]), token);
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using var client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Family = GraphProtocol.Family,
            Settings = new DatabaseConnectionSettings { Database = "graph", EndPoint = listener.EndPoint },
            ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
        });
        await using var connection = await client.RentAsync(token);

        // Act / Assert
        foreach (var (query, code) in new[] { ("INSERT (:N {p:1})", "CG03012"), ("INSERT (:N:I)", "CG03003"), ("INSERT (:N:I {p:'bad'})", "CG03003") })
        {
            var error = await Should.ThrowAsync<DatabaseClientException>(async () => await connection.ExecuteAsync(query, cancellationToken: token));
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            (await connection.ExecuteAsync("RETURN 1", cancellationToken: token)).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        }
        (await connection.ExecuteAsync("INSERT (:N:I {p:1,extra:TRUE})", cancellationToken: token)).AffectedCount.ShouldBe(1);
        var removal = await Should.ThrowAsync<DatabaseClientException>(async () => await connection.ExecuteAsync("MATCH (n:N) REMOVE n:I", cancellationToken: token));
        removal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03012");
        (await connection.ExecuteAsync("INSERT (:Unknown {extra:1})", cancellationToken: token)).AffectedCount.ShouldBe(1);
        await schema.DropGraphTypeAsync(token);
        (await connection.ExecuteAsync("INSERT (:N)", cancellationToken: token)).AffectedCount.ShouldBe(1);
    }
}
