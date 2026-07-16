using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.KeyValuePair;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// The key-value wire end-to-end: the full stack composed the builder-first way —
/// file-backed key-value engine registered by <c>AddKeyValueDatabase</c>, a real
/// TCP loopback listener, the model server registered by <c>AddKeyValueServer</c>,
/// the hosting application built from the root builder — driven with the typed
/// key-value client, including restart recovery over the real file sets.
/// </summary>
/// <remarks>
/// The <c>Database.Application</c> executable stays SQL-only — multi-model host
/// composition (one process fronting several model servers) is a later
/// deliverable; this suite composes the key-value application in-test the same
/// way that executable composes the SQL one.
/// </remarks>
public sealed class KeyValueApplicationEndToEndTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-kv-e2e", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private const string DatabaseName = "kv";

    private static CancellationToken TestTimeout(int seconds = 30)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    /// <summary>
    /// The TCP listener binds lazily on the accept loop's first accept; poll until
    /// the OS-assigned port is observable.
    /// </summary>
    private static async Task<int> WaitForBoundPortAsync(TcpConnectionListener listener)
    {
        long deadline = Environment.TickCount64 + 15_000;

        while (Environment.TickCount64 < deadline)
        {
            if (listener.EndPoint is IPEndPoint { Port: > 0 } endpoint)
            {
                return endpoint.Port;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The TCP listener did not bind within the budget.");
    }

    private static IKeyValueClient CreateClient(int port)
        => KeyValueClient.Create(new KeyValueClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = DatabaseName,
                Principal = "e2e",
                EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            },
            ConnectionFactory = new TcpConnectionFactory(),
        });

    /// <summary>
    /// Composes the key-value application the builder-first way: engine verb,
    /// TCP listener, server verb, build. The caller owns engine and listener
    /// disposal (composition-root ownership).
    /// </summary>
    private (KeyValueDatabaseEngine Engine, TcpConnectionListener Listener, IDatabaseApplication Application) Compose()
    {
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();

        KeyValueDatabaseEngine engine = builder.AddKeyValueDatabase(options =>
        {
            options.EngineName = "kv";
            options.RootPath = _rootPath;
        });

        var listener = new TcpConnectionListener(new TcpConnectionListenerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });

        builder.AddKeyValueServer(engine, options => options.Listener = listener);

        return (engine, listener, builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - E2E: key-value over TCP round-trips CRUD/CAS/scan and data survives a restart")]
    public async Task EndToEnd_KeyValueOverTcpWithRestart_ShouldServeAndRecover()
    {
        long swappedETag;

        // ---- First composition: serve CRUD/CAS/scan over real TCP loopback. ----
        {
            var (engine, listener, application) = Compose();

            await using (engine)
            await using (listener)
            {
                // Code-first provisioning before the endpoint accepts (the area
                // principle: the wire carries no database-management verbs).
                await engine.CreateDatabaseAsync(DatabaseName, TestTimeout());

                await application.StartAsync(TestTimeout());
                int port = await WaitForBoundPortAsync(listener);

                await using (var client = CreateClient(port))
                await using (var connection = await client.ConnectAsync(TestTimeout()))
                {
                    // CRUD round-trip.
                    long etag = await connection.PutAsync(Bytes("user:1"), Bytes("ada"), TestTimeout());
                    var entry = await connection.GetAsync(Bytes("user:1"), TestTimeout());
                    Text(entry!.Value.Value).ShouldBe("ada");
                    entry.Value.ETag.ShouldBe(etag);

                    // Compare-and-swap: stale miss (first-class), current applies.
                    var stale = await connection.PutAsync(
                        Bytes("user:1"), Bytes("ignored"), KeyValueWriteCondition.IfETagMatches(etag + 1000), TestTimeout());
                    stale.Applied.ShouldBeFalse();
                    stale.ETag.ShouldBe(etag);

                    var swap = await connection.PutAsync(
                        Bytes("user:1"), Bytes("ada-2"), KeyValueWriteCondition.IfETagMatches(etag), TestTimeout());
                    swap.Applied.ShouldBeTrue();
                    swappedETag = swap.ETag!.Value;

                    // A few more entries, one deleted — the scan shows the survivors.
                    await connection.PutAsync(Bytes("user:2"), Bytes("grace"), TestTimeout());
                    await connection.PutAsync(Bytes("vendor:1"), Bytes("acme"), TestTimeout());
                    (await connection.TryDeleteAsync(Bytes("vendor:1"), TestTimeout())).ShouldBeTrue();

                    IReadOnlyList<KeyValueClientEntry> users = await connection.ScanAsync(
                        new KeyValueScanRange { Prefix = Bytes("user:") }, TestTimeout());
                    users.Count.ShouldBe(2);
                    Text(users[0].Key).ShouldBe("user:1");
                    Text(users[1].Key).ShouldBe("user:2");
                }

                await application.StopAsync(TestTimeout());
            }
        }

        // ---- Second composition over the same root: restart recovery. ----
        {
            var (engine, listener, application) = Compose();

            await using (engine)
            await using (listener)
            {
                await engine.OpenDatabaseAsync(DatabaseName, TestTimeout());

                await application.StartAsync(TestTimeout());
                int port = await WaitForBoundPortAsync(listener);

                await using (var client = CreateClient(port))
                await using (var connection = await client.ConnectAsync(TestTimeout()))
                {
                    // The committed state survived the restart — value, etag, and
                    // the primary index (a get is an index seek) all recovered.
                    var recovered = await connection.GetAsync(Bytes("user:1"), TestTimeout());
                    Text(recovered!.Value.Value).ShouldBe("ada-2");
                    recovered.Value.ETag.ShouldBe(swappedETag);

                    (await connection.ExistsAsync(Bytes("vendor:1"), TestTimeout())).ShouldBeFalse();

                    IReadOnlyList<KeyValueClientEntry> all = await connection.ScanAsync(cancellationToken: TestTimeout());
                    all.Count.ShouldBe(2);

                    // And the recovered database accepts new conditional writes.
                    var post = await connection.PutAsync(
                        Bytes("user:1"), Bytes("ada-3"), KeyValueWriteCondition.IfETagMatches(swappedETag), TestTimeout());
                    post.Applied.ShouldBeTrue();
                }

                await application.StopAsync(TestTimeout());
            }
        }
    }
}
