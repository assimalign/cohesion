using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

internal sealed class BlobClientTestHarness : IAsyncDisposable
{
    private BlobClientTestHarness(BlobDatabaseEngine engine, BlobDatabase database, BlobDatabaseSession session, BlobContainer container,
        IDuplexConnectionListener listener, BlobDatabaseServer server, RecordingConnectionFactory factory, BlobClient client)
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
    internal IDuplexConnectionListener Listener { get; }
    internal BlobDatabaseServer Server { get; }
    internal RecordingConnectionFactory Factory { get; }
    internal BlobClient Client { get; }

    internal static async Task<BlobClientTestHarness> StartAsync(CancellationToken token, bool tcp = false)
    {
        var engine = BlobDatabaseEngine.Create("blob-engine", new());
        var database = await engine.CreateDatabaseAsync("app", token);
        var session = await database.CreateSessionAsync(token);
        var container = await session.CreateContainerAsync("files", token);
        IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        var server = BlobDatabaseServer.Create(engine, new()
        {
            Listener = listener,
            ShutdownDrainTimeout = tcp ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(100)
        });
        await server.StartAsync(token);
        var factory = new RecordingConnectionFactory(tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory());
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
