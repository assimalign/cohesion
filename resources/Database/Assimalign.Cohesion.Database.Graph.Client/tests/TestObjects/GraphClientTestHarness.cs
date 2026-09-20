using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

internal sealed class GraphClientTestHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    private GraphClientTestHarness(GraphDatabaseEngine engine, IGraphDatabase database,
        InMemoryConnectionListener listener, GraphDatabaseServer server, IGraphClient client)
    {
        Engine = engine;
        Database = database;
        Listener = listener;
        Server = server;
        Client = client;
    }

    internal GraphDatabaseEngine Engine { get; }
    internal IGraphDatabase Database { get; }
    internal InMemoryConnectionListener Listener { get; }
    internal GraphDatabaseServer Server { get; }
    internal IGraphClient Client { get; }
    internal CancellationToken Token => _timeout.Token;

    internal static async Task<GraphClientTestHarness> StartAsync()
    {
        var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        var listener = new InMemoryConnectionListener();
        var server = GraphDatabaseServer.Create(engine, new() { Listener = listener, ShutdownDrainTimeout = TimeSpan.FromSeconds(2) });
        await server.StartAsync(CancellationToken.None);
        var client = GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "graph", Principal = "tester", EndPoint = listener.EndPoint, MaxPoolSize = 1,
            },
            ConnectionFactory = listener.CreateFactory(),
        });
        return new(engine, database, listener, server, client);
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

