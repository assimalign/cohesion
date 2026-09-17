using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCatalogClientTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Catalog client: all definitions cross the TCP protocol")]
    public async Task Execute_ShowCatalog_ShouldReturnTypedMetadataOverTcp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("own", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        var schema = GraphSchema.Open(database, session);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        var relationship = new GraphRelationshipTypeMetadata(Guid.NewGuid(), "KNOWS");
        await schema.SaveLabelAsync(label, timeout.Token);
        await schema.SaveRelationshipTypeAsync(relationship, timeout.Token);
        await schema.SavePropertyKeyAsync(new(label.Id, "name", DatabaseType.String, true), timeout.Token);
        await schema.CreateIndexAsync("Person", "by_name", "name", timeout.Token);

        var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var client = CreateClient(listener, "own");
        await using var connection = await client.RentAsync(timeout.Token);

        var labels = await connection.ExecuteAsync("SHOW LABELS", cancellationToken: timeout.Token);
        labels.Columns.Select(column => column.Name).ShouldBe(["DATABASE_NAME", "LABEL_ID", "LABEL_NAME"]);
        labels.Columns[1].Type.ShouldBe(DatabaseType.Guid);
        labels.Rows.ShouldHaveSingleItem().ShouldBe(["own", label.Id, "Person"]);
        (await connection.ExecuteAsync("SHOW RELATIONSHIP TYPES", cancellationToken: timeout.Token))
            .Rows.ShouldHaveSingleItem().ShouldBe(["own", relationship.Id, "KNOWS"]);
        (await connection.ExecuteAsync("SHOW PROPERTY KEYS", cancellationToken: timeout.Token))
            .Rows.ShouldHaveSingleItem().ShouldBe(["own", "LABEL", label.Id, "Person", "name", "String", true]);
        (await connection.ExecuteAsync("SHOW INDEXES", cancellationToken: timeout.Token))
            .Rows.ShouldHaveSingleItem().ShouldBe(["own", label.Id, "Person", "by_name", "name", false]);
        var ownership = await connection.ExecuteAsync("SHOW OBJECT OWNERSHIP", cancellationToken: timeout.Token);
        ownership.Rows.Count.ShouldBe(4);
        ownership.Rows.ShouldAllBe(row => (string)row[6]! == "Adhoc" && row[7] == null);

        await schema.SaveLabelAsync(new(Guid.NewGuid(), "Later"), timeout.Token);
        (await connection.ExecuteAsync("SHOW LABELS", cancellationToken: timeout.Token)).Rows.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Catalog client: startup selects one database")]
    public async Task Execute_WithTwoDatabases_ShouldKeepMetadataScoped()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create(new());
        foreach (var name in new[] { "own", "other" })
        {
            var database = (IGraphDatabase)await engine.CreateDatabaseAsync(name, timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await GraphSchema.Open(database, session).SaveLabelAsync(new(Guid.NewGuid(), name + "_label"), timeout.Token);
        }
        var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var ownClient = CreateClient(listener, "own");
        await using var otherClient = CreateClient(listener, "other");
        await using var own = await ownClient.RentAsync(timeout.Token);
        await using var other = await otherClient.RentAsync(timeout.Token);
        (await own.ExecuteAsync("SHOW LABELS", cancellationToken: timeout.Token)).Rows.ShouldHaveSingleItem()[2].ShouldBe("own_label");
        (await other.ExecuteAsync("SHOW LABELS", cancellationToken: timeout.Token)).Rows.ShouldHaveSingleItem()[2].ShouldBe("other_label");

        var error = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await own.ExecuteAsync("SHOW LABELS FROM other", cancellationToken: timeout.Token));
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        (await own.ExecuteAsync("SHOW LABELS", cancellationToken: timeout.Token)).Rows.ShouldHaveSingleItem()[0].ShouldBe("own");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Catalog client: writes fail without breaking the connection")]
    [InlineData("SHOW LABELS DELETE n")]
    [InlineData("SHOW RELATIONSHIP TYPES SET name = 'x'")]
    [InlineData("SHOW PROPERTY KEYS REMOVE name")]
    [InlineData("SHOW INDEXES DROP INDEX by_name")]
    [InlineData("SHOW OBJECT OWNERSHIP CREATE (n)")]
    public async Task Execute_CatalogMutation_ShouldReturnReadOnlyDiagnostic(string statement)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create(new());
        await engine.CreateDatabaseAsync("own", timeout.Token);
        var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var client = CreateClient(listener, "own");
        await using var connection = await client.RentAsync(timeout.Token);

        var error = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await connection.ExecuteAsync(statement, cancellationToken: timeout.Token));
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldContain("GQL0007: Graph catalog introspection is read-only.", Case.Sensitive);
        (await connection.ExecuteAsync("SHOW LABELS", cancellationToken: timeout.Token)).Rows.ShouldBeEmpty();

        var unsupported = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await connection.ExecuteAsync("CREATE (n:Injected)", cancellationToken: timeout.Token));
        unsupported.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        unsupported.Message.ShouldContain("The graph wire server supports catalog SHOW statements only.", Case.Sensitive);
        (await connection.ExecuteAsync("SHOW LABELS", cancellationToken: timeout.Token)).Rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Catalog server: stop is terminal and engine ownership is retained")]
    public async Task Stop_WhenStarted_ShouldReleaseListenerAndKeepEngine()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create(new());
        await engine.CreateDatabaseAsync("own", timeout.Token);
        var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = GraphDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var client = CreateClient(listener, "own");
        await using var connection = await client.RentAsync(timeout.Token);
        (await connection.ExecuteAsync("SHOW LABELS", cancellationToken: timeout.Token)).Rows.ShouldBeEmpty();
        server.Context.Sessions.ShouldHaveSingleItem();
        await server.StopAsync(timeout.Token);
        server.Context.Sessions.ShouldBeEmpty();
        await server.StopAsync(timeout.Token);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await server.StartAsync(timeout.Token));
        engine.TryGetDatabase("own", out _).ShouldBeTrue();
    }

    private static IDatabaseClient CreateClient(TcpConnectionListener listener, string database)
        => DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = database, EndPoint = listener.EndPoint },
            ConnectionFactory = new TcpConnectionFactory(),
        });
}
