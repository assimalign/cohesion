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
    private BlobClientTestHarness(BlobDatabaseEngine engine, BlobDatabase database, BlobDatabaseSession session, BlobContainer container,
        InMemoryConnectionListener listener, BlobDatabaseServer server, RecordingConnectionFactory factory, BlobClient client)
    {
        Engine = engine;
        Database = database;
        Session = session;
        Container = container;
        Listener = listener;
        Server = server;
        Factory = factory;
        Client = client;
    }

    internal BlobDatabaseEngine Engine { get; }
    internal BlobDatabase Database { get; }

    /// <summary>
    /// Gets the in-process session <see cref="Container"/> is bound to: the container operations are a
    /// session's (owner decision 32), and the harness's run in autocommit in this one.
    /// </summary>
    internal BlobDatabaseSession Session { get; }
    internal BlobContainer Container { get; }
    internal InMemoryConnectionListener Listener { get; }
    internal BlobDatabaseServer Server { get; }
    internal RecordingConnectionFactory Factory { get; }
    internal BlobClient Client { get; }

    internal static async Task<BlobClientTestHarness> StartAsync(CancellationToken token)
    {
        var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("app", token);
        var session = await database.CreateSessionAsync(token);
        var container = await session.CreateContainerAsync("files", token);
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
        return new(engine, database, session, container, listener, server, factory, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
        await Session.DisposeAsync();
        await Engine.DisposeAsync();
    }
}

internal sealed class RecordingConnectionFactory : ConnectionFactory
{
    private readonly ConnectionFactory _inner;

    /// <summary>Initializes a new instance of the <see cref="RecordingConnectionFactory"/> class.</summary>
    /// <param name="inner">The connection factory whose connections are recorded.</param>
    public RecordingConnectionFactory(ConnectionFactory inner)
    {
        _inner = inner;
    }

    internal Connection? LastConnection { get; private set; }
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;

    public override async ValueTask<Connection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        LastConnection = await _inner.ConnectAsync(endPoint, cancellationToken);
        return LastConnection;
    }
}
