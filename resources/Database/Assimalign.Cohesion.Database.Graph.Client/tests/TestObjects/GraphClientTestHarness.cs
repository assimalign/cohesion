using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

internal sealed class GraphClientTestHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private GraphClientTestHarness(GraphDatabaseEngine engine, GraphDatabase database,
        IDuplexConnectionListener listener, IDuplexConnectionFactory connectionFactory, GraphDatabaseServer server, GraphClient client)
    {
        Engine = engine;
        Database = database;
        Listener = listener;
        ConnectionFactory = connectionFactory;
        Server = server;
        Client = client;
    }

    internal GraphDatabaseEngine Engine { get; }
    internal GraphDatabase Database { get; }
    internal IDuplexConnectionListener Listener { get; }
    internal IDuplexConnectionFactory ConnectionFactory { get; }
    internal GraphDatabaseServer Server { get; }
    internal GraphClient Client { get; }
    internal CancellationToken Token => _timeout.Token;

    internal static async Task<GraphClientTestHarness> StartAsync(bool tcp = false, Action<GraphFunctionCollection>? configureFunctions = null)
    {
        GraphDatabaseEngine engine;
        if (configureFunctions is null) { engine = GraphDatabaseEngine.Create("graph-engine", new()); }
        else
        {
            var builder = GraphDatabaseEngine.CreateBuilder("graph-engine");
            configureFunctions(builder.Functions);
            engine = await builder.BuildAsync();
        }
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        var server = GraphDatabaseServer.Create(engine, new() { Listener = listener, ShutdownDrainTimeout = TimeSpan.FromSeconds(2) });
        await server.StartAsync(CancellationToken.None);
        IDuplexConnectionFactory connectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory();
        var client = GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "graph", Principal = "tester", EndPoint = listener.EndPoint, MaxPoolSize = 1,
            },
            ConnectionFactory = connectionFactory,
        });
        return new(engine, database, listener, connectionFactory, server, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
        await Engine.DisposeAsync();
        _timeout.Dispose();
    }
}

