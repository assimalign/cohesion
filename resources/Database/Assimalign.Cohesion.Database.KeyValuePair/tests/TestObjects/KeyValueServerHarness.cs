using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// A live key-value engine + in-memory listener + running
/// <see cref="KeyValueDatabaseServer"/>, with a raw protocol client, for
/// wire-level tests of the model's own server machinery.
/// </summary>
internal sealed class KeyValueServerHarness : IAsyncDisposable
{
    private KeyValueServerHarness(KeyValueDatabaseEngine engine, InMemoryConnectionListener listener, KeyValueDatabaseServer server)
    {
        Engine = engine;
        Listener = listener;
        Server = server;
    }

    public KeyValueDatabaseEngine Engine { get; }

    public InMemoryConnectionListener Listener { get; }

    public KeyValueDatabaseServer Server { get; }

    public const string DatabaseName = "kv";

    public static async Task<KeyValueServerHarness> StartAsync(Action<KeyValueDatabaseServerOptions>? configure = null)
    {
        var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "kv-wire" });
        await engine.CreateDatabaseAsync(DatabaseName);

        var listener = new InMemoryConnectionListener();
        var options = new KeyValueDatabaseServerOptions { Listener = listener };

        configure?.Invoke(options);

        var server = KeyValueDatabaseServer.Create(engine, options);
        await server.StartAsync();

        return new KeyValueServerHarness(engine, listener, server);
    }

    /// <summary>
    /// Dials the server, returning a raw protocol client over the in-memory pair.
    /// </summary>
    public async Task<KeyValueProtocolClient> DialAsync()
    {
        Connection connection = await Listener.CreateFactory().ConnectAsync(Listener.EndPoint, TestTimeout.Token());
        return new KeyValueProtocolClient(connection);
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
