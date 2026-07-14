using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.KeyValuePair;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// A live key-value engine + in-memory listener + running server + typed
/// key-value client, for typed-client end-to-end tests without sockets.
/// </summary>
internal sealed class KeyValueClientTestHarness : IAsyncDisposable
{
    private KeyValueClientTestHarness(KeyValueDatabaseEngine engine, InMemoryConnectionListener listener, KeyValueDatabaseServer server, IKeyValueClient client)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
        Client = client;
    }

    public KeyValueDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public KeyValueDatabaseServer Server { get; }

    public IKeyValueClient Client { get; }

    public const string DatabaseName = "kv";

    public static async Task<KeyValueClientTestHarness> StartAsync(
        Action<KeyValueDatabaseServerOptions>? configureServer = null,
        IKeyValueClientObserver? observer = null)
    {
        var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "kv-typed-client-e2e" });
        await engine.CreateDatabaseAsync(DatabaseName);

        var listener = new InMemoryConnectionListener();
        var serverOptions = new KeyValueDatabaseServerOptions { Listener = listener };

        configureServer?.Invoke(serverOptions);

        var server = KeyValueDatabaseServer.Create(engine, serverOptions);
        await server.StartAsync();

        var client = KeyValueClient.Create(new KeyValueClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = DatabaseName,
                Principal = "tester",
                EndPoint = listener.EndPoint,
            },
            ConnectionFactory = listener.CreateFactory(),
            Observer = observer,
        });

        return new KeyValueClientTestHarness(engine, listener, server, client);
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
