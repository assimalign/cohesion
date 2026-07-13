using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// A live SQL engine + in-memory listener + running server + typed SQL client, with
/// a seeded <c>users</c> table, for typed-client end-to-end tests without sockets.
/// </summary>
internal sealed class SqlClientTestHarness : IAsyncDisposable
{
    private SqlClientTestHarness(SqlDatabaseEngine engine, InMemoryConnectionListener listener, IDatabaseServer server, ISqlClient client)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
        Client = client;
    }

    public SqlDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public IDatabaseServer Server { get; }

    public ISqlClient Client { get; }

    public const string DatabaseName = "app";

    public static async Task<SqlClientTestHarness> StartAsync(
        Action<DatabaseServerOptions>? configureServer = null,
        Action<DatabaseConnectionSettings>? configureSettings = null,
        ISqlClientObserver? observer = null)
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-typed-client-e2e" });
        await engine.StartAsync();

        var database = await engine.CreateDatabaseAsync(DatabaseName);

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE users (id INT NOT NULL, name VARCHAR(100), score BIGINT)");
            await session.ExecuteAsync("INSERT INTO users (id, name, score) VALUES (1, 'ada', 100), (2, 'grace', 200)");
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

        var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = settings,
            ConnectionFactory = listener.CreateFactory(),
            Observer = observer,
        });

        return new SqlClientTestHarness(engine, listener, server, client);
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
