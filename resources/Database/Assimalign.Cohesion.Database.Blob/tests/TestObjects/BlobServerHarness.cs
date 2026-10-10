using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// A live Blob engine + in-memory listener + running <see cref="BlobDatabaseServer"/>, with a
/// raw protocol client, for wire-level tests of the model's own server machinery. The harness
/// owns the engine, whether it built the engine or was handed one.
/// </summary>
internal sealed class BlobServerHarness : IAsyncDisposable
{
    private BlobServerHarness(BlobDatabaseEngine engine, InMemoryConnectionListener listener, BlobDatabaseServer server)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
    }

    public BlobDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public BlobDatabaseServer Server { get; }

    public const string DatabaseName = "objects";

    public const string ContainerName = "files";

    public static async Task<BlobServerHarness> StartAsync(
        Action<BlobDatabaseServerOptions>? configure = null,
        Action<BlobDatabaseEngineOptions>? configureEngine = null,
        string engineName = "blob-wire")
    {
        var engineOptions = new BlobDatabaseEngineOptions();
        configureEngine?.Invoke(engineOptions);
        var engine = BlobDatabaseEngine.Create(engineName, engineOptions);
        var database = await engine.CreateDatabaseAsync(DatabaseName);
        await AutocommitContainer.CreateAsync(database, ContainerName, TestTimeout.Token());
        return await StartAsync(engine, configure);
    }

    public static async Task<BlobServerHarness> StartAsync(BlobDatabaseEngine engine, Action<BlobDatabaseServerOptions>? configure = null)
    {
        var listener = new InMemoryConnectionListener();
        var options = new BlobDatabaseServerOptions { Listener = listener };

        configure?.Invoke(options);

        var server = BlobDatabaseServer.Create(engine, options);
        await server.StartAsync();

        return new BlobServerHarness(engine, listener, server);
    }

    /// <summary>
    /// Dials the server, returning a raw protocol client over the in-memory pair.
    /// </summary>
    public async Task<BlobProtocolClient> DialAsync()
    {
        Connection connection = await Listener.CreateFactory().ConnectAsync(Listener.EndPoint, TestTimeout.Token());
        return new BlobProtocolClient(connection);
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
