using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// A live graph engine + in-memory listener + running <see cref="GraphDatabaseServer"/>, with a
/// raw protocol client, for wire-level tests of the model's own server machinery.
/// </summary>
internal sealed class GraphServerHarness : IAsyncDisposable
{
    private GraphServerHarness(GraphDatabaseEngine engine, InMemoryConnectionListener listener, GraphDatabaseServer server)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
    }

    public GraphDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public GraphDatabaseServer Server { get; }

    public const string DatabaseName = "graph";

    public static async Task<GraphServerHarness> StartAsync(
        Action<GraphDatabaseServerOptions>? configure = null,
        Action<GraphDatabaseEngineOptions>? configureEngine = null)
    {
        var engineOptions = new GraphDatabaseEngineOptions { EngineName = "graph-wire" };
        configureEngine?.Invoke(engineOptions);
        var engine = GraphDatabaseEngine.Create(engineOptions);
        await engine.CreateDatabaseAsync(DatabaseName);

        var listener = new InMemoryConnectionListener();
        var options = new GraphDatabaseServerOptions { Listener = listener };

        configure?.Invoke(options);

        var server = GraphDatabaseServer.Create(engine, options);
        await server.StartAsync();

        return new GraphServerHarness(engine, listener, server);
    }

    /// <summary>
    /// Dials the server, returning a raw protocol client over the in-memory pair.
    /// </summary>
    public async Task<GraphProtocolClient> DialAsync()
    {
        Connection connection = await Listener.CreateFactory().ConnectAsync(Listener.EndPoint, TestTimeout.Token());
        return new GraphProtocolClient(connection);
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
