using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Composes a real hosted database — a live SQL engine fronted by a
/// <see cref="SqlDatabaseServer"/> over the in-memory listener, wrapped by the
/// application as its endpoint service — plus a client, so a test can start the
/// host and drive a served round-trip without sockets.
/// </summary>
internal sealed class DatabaseHostTestHarness : IAsyncDisposable
{
    private DatabaseHostTestHarness(SqlDatabaseEngine engine, InMemoryConnectionListener listener, SqlDatabaseServer server, DatabaseApplication application, IDatabaseClient client)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
        Application = application;
        Client = client;
    }

    public SqlDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public SqlDatabaseServer Server { get; }

    public DatabaseApplication Application { get; }

    public IDatabaseClient Client { get; }

    public const string DatabaseName = "app";

    /// <summary>
    /// Composes the full hosted stack without starting the host. The engine is a
    /// data machine — operational from creation — so seeding happens here, before
    /// the application (and therefore the endpoint) ever starts.
    /// </summary>
    public static async Task<DatabaseHostTestHarness> CreateAsync(Action<DatabaseApplicationOptions>? configure = null)
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-host-e2e" });

        var database = await engine.CreateDatabaseAsync(DatabaseName);

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE users (id INT NOT NULL, name VARCHAR(100))");
            await session.ExecuteAsync("INSERT INTO users (id, name) VALUES (1, 'ada'), (2, 'grace')");
        }

        var listener = new InMemoryConnectionListener();
        var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });

        var options = new DatabaseApplicationOptions();
        options.Servers.Add(server);
        configure?.Invoke(options);

        var application = new DatabaseApplication(options);

        var client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = DatabaseName, EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });

        return new DatabaseHostTestHarness(engine, listener, server, application, client);
    }

    public Task StartHostAsync(CancellationToken cancellationToken = default)
        => ((IHost)Application).StartAsync(cancellationToken);

    public Task StopHostAsync(CancellationToken cancellationToken = default)
        => ((IHost)Application).StopAsync(cancellationToken);

    public static CancellationToken Timeout(int seconds = 10)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await ((IHost)Application).StopAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
        await Engine.DisposeAsync();
    }
}
