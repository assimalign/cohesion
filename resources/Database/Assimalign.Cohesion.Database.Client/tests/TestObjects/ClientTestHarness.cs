using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Server;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>
/// A live SQL engine + in-memory listener + running server + pooling client, with
/// a seeded <c>users</c> table, for client end-to-end tests without sockets.
/// </summary>
internal sealed class ClientTestHarness : IAsyncDisposable
{
    private ClientTestHarness(SqlDatabaseEngine engine, InMemoryConnectionListener listener, IDatabaseServer server, IDatabaseClient client)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
        Client = client;
    }

    public SqlDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public IDatabaseServer Server { get; }

    public IDatabaseClient Client { get; }

    public const string DatabaseName = "app";

    public static async Task<ClientTestHarness> StartAsync(
        Action<DatabaseServerOptions>? configureServer = null,
        Action<DatabaseConnectionSettings>? configureSettings = null)
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-client-e2e" });
        await engine.StartAsync();

        var database = await engine.CreateDatabaseAsync(DatabaseName);

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE users (id INT NOT NULL, name VARCHAR(100))");
            await session.ExecuteAsync("INSERT INTO users (id, name) VALUES (1, 'ada'), (2, 'grace')");
        }

        var listener = new InMemoryConnectionListener();
        var serverOptions = new DatabaseServerOptions { Listener = listener };

        serverOptions.Engines.Add(engine);
        configureServer?.Invoke(serverOptions);

        var server = DatabaseServer.Create(serverOptions);
        await server.StartAsync();

        var settings = new DatabaseConnectionSettings
        {
            Database = DatabaseName,
            Principal = "tester",
            EndPoint = listener.EndPoint,
        };

        configureSettings?.Invoke(settings);

        var client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = settings,
            ConnectionFactory = listener.CreateFactory(),
        });

        return new ClientTestHarness(engine, listener, server, client);
    }

    /// <summary>
    /// A bounded token so a hung exchange fails the test instead of the run.
    /// </summary>
    public static CancellationToken Timeout(int seconds = 10)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    /// <summary>
    /// Polls until the condition holds or the timeout lapses.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        CancellationToken token = Timeout(timeoutSeconds);

        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(10, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
        await Engine.DisposeAsync();
    }
}
