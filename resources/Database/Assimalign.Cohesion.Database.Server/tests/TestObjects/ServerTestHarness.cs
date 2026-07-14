using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Server.Tests;

/// <summary>
/// A fake model engine + in-memory listener + running shared-core server, for
/// end-to-end protocol tests without sockets and without any real model.
/// </summary>
internal sealed class ServerTestHarness : IAsyncDisposable
{
    private ServerTestHarness(FakeModelEngine engine, InMemoryConnectionListener listener, TestDatabaseServer server)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
    }

    public FakeModelEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public TestDatabaseServer Server { get; }

    public const string DatabaseName = "app";

    public static async Task<ServerTestHarness> StartAsync(Action<DatabaseServerOptions>? configure = null)
    {
        var engine = new FakeModelEngine(DatabaseName);
        var listener = new InMemoryConnectionListener();
        var options = new DatabaseServerOptions { Listener = listener };

        configure?.Invoke(options);

        var server = new TestDatabaseServer(engine, options);
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
