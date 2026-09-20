using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

internal sealed class BlobClientTestHarness : IAsyncDisposable
{
    private BlobClientTestHarness(BlobDatabaseEngine engine, IBlobDatabase database, IBlobContainer container,
        InMemoryConnectionListener listener, BlobDatabaseServer server, RecordingConnectionFactory factory, IBlobClient client)
    {
        Engine = engine;
        Database = database;
        Container = container;
        Listener = listener;
        Server = server;
        Factory = factory;
        Client = client;
    }

    internal BlobDatabaseEngine Engine { get; }
    internal IBlobDatabase Database { get; }
    internal IBlobContainer Container { get; }
    internal InMemoryConnectionListener Listener { get; }
    internal BlobDatabaseServer Server { get; }
    internal RecordingConnectionFactory Factory { get; }
    internal IBlobClient Client { get; }

    internal static async Task<BlobClientTestHarness> StartAsync(CancellationToken token)
    {
        var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("app", token);
        var container = await database.CreateContainerAsync("files", token);
        var listener = new InMemoryConnectionListener();
        var server = BlobDatabaseServer.Create(engine, new()
        {
            Listener = listener,
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100)
        });
        await server.StartAsync(token);
        var factory = new RecordingConnectionFactory(listener.CreateFactory());
        var client = BlobClient.Create(new BlobClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "app", Principal = "tester", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = factory
        });
        return new(engine, database, container, listener, server, factory, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
        await Engine.DisposeAsync();
    }
}

internal sealed class RecordingConnectionFactory(ConnectionFactory inner) : ConnectionFactory
{
    internal Connection? LastConnection { get; private set; }
    public override ConnectionCapabilities Capabilities => inner.Capabilities;

    public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        LastConnection = await inner.ConnectAsync(endPoint, cancellationToken);
        return LastConnection;
    }
}
