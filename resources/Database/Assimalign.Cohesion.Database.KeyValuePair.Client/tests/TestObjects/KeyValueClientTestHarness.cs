using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.KeyValuePair;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// A live key-value engine, InMemory or TCP listener, running server and typed client.
/// </summary>
internal sealed class KeyValueClientTestHarness : IAsyncDisposable
{
    private KeyValueClientTestHarness(KeyValueDatabaseEngine engine, IDuplexConnectionListener listener, KeyValueDatabaseServer server, KeyValueClient client, IDuplexConnectionFactory factory)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
        Client = client;
        Factory = factory;
    }

    public KeyValueDatabaseEngine Engine { get; }

    public IDuplexConnectionListener Listener { get; }

    public KeyValueDatabaseServer Server { get; }

    public KeyValueClient Client { get; }

    public IDuplexConnectionFactory Factory { get; }

    public const string DatabaseName = "kv";

    public static async Task<KeyValueClientTestHarness> StartAsync(
        Action<KeyValueDatabaseServerOptions>? configureServer = null,
        KeyValueClientObserver? observer = null,
        Action<DatabaseConnectionSettings>? configureSettings = null,
        bool tcp = false,
        Action<KeyValueFunctionCollection>? configureFunctions = null)
    {
        var builder = KeyValueDatabaseEngine.CreateBuilder("kv-typed-client-e2e");
        configureFunctions?.Invoke(builder.Functions);
        var engine = await builder.BuildAsync();
        await engine.CreateDatabaseAsync(DatabaseName);

        IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        var serverOptions = new KeyValueDatabaseServerOptions { Listener = listener };

        configureServer?.Invoke(serverOptions);

        var server = KeyValueDatabaseServer.Create(engine, serverOptions);
        await server.StartAsync();

        var settings = new DatabaseConnectionSettings
        {
            Database = DatabaseName,
            Principal = "tester",
            EndPoint = listener.EndPoint,
        };

        configureSettings?.Invoke(settings);

        IDuplexConnectionFactory factory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory();
        var client = KeyValueClient.Create(new KeyValueClientOptions
        {
            Settings = settings,
            ConnectionFactory = factory,
            Observer = observer,
        });

        return new KeyValueClientTestHarness(engine, listener, server, client, factory);
    }

    /// <summary>
    /// A bounded token so a hung exchange fails the test instead of the run.
    /// </summary>
    public static CancellationToken Timeout(int seconds = 10)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
        await Engine.DisposeAsync();
    }
}
