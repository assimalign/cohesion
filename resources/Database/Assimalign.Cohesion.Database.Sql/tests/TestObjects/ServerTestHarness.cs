using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// A live SQL engine + in-memory listener + running <see cref="SqlDatabaseServer"/>,
/// with a seeded <c>users</c> table, for end-to-end protocol tests without sockets.
/// </summary>
internal sealed class ServerTestHarness : IAsyncDisposable
{
    private ServerTestHarness(SqlDatabaseEngine engine, InMemoryConnectionListener listener, SqlDatabaseServer server)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
    }

    public SqlDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public SqlDatabaseServer Server { get; }

    public const string DatabaseName = "app";

    public static async Task<ServerTestHarness> StartAsync(Action<SqlDatabaseServerOptions>? configure = null)
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-e2e" });

        var database = await engine.CreateDatabaseAsync(DatabaseName);

        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE users (id INT NOT NULL, name VARCHAR(100))");
            await session.ExecuteAsync("INSERT INTO users (id, name) VALUES (1, 'ada'), (2, 'grace')");
        }

        var listener = new InMemoryConnectionListener();
        var options = new SqlDatabaseServerOptions { Listener = listener };

        configure?.Invoke(options);

        var server = SqlDatabaseServer.Create(engine, options);
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
