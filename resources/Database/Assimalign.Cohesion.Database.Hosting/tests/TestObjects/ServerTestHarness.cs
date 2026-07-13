using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A live SQL engine + in-memory listener + running server, with a seeded
/// <c>users</c> table, for end-to-end protocol tests without sockets.
/// </summary>
internal sealed class ServerTestHarness : IAsyncDisposable
{
    private ServerTestHarness(SqlDatabaseEngine engine, InMemoryConnectionListener listener, IDatabaseServer server)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
    }

    public SqlDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public IDatabaseServer Server { get; }

    public const string DatabaseName = "app";

    public static async Task<ServerTestHarness> StartAsync(Action<DatabaseServerOptions>? configure = null)
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-e2e" });
        await engine.StartAsync();

        var database = await engine.CreateDatabaseAsync(DatabaseName);

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE users (id INT NOT NULL, name VARCHAR(100))");
            await session.ExecuteAsync("INSERT INTO users (id, name) VALUES (1, 'ada'), (2, 'grace')");
        }

        var listener = new InMemoryConnectionListener();
        var options = new DatabaseServerOptions { Listener = listener };

        options.Engines.Add(engine);
        configure?.Invoke(options);

        var server = DatabaseServer.Create(options);
        await server.StartAsync();

        return new ServerTestHarness(engine, listener, server);
    }

    /// <summary>
    /// Dials the server, returning a raw protocol client over the in-memory pair.
    /// </summary>
    public async Task<ProtocolTestClient> DialAsync()
    {
        Connection connection = await Listener.CreateFactory().ConnectAsync(Listener.EndPoint, TestTimeout.Token());
        return new ProtocolTestClient(connection);
    }

    /// <summary>
    /// Polls until the condition holds or the timeout lapses (session teardown is
    /// asynchronous with respect to the last frame).
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        CancellationToken token = TestTimeout.Token(timeoutSeconds);

        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(10, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
        await Engine.DisposeAsync();
    }
}
