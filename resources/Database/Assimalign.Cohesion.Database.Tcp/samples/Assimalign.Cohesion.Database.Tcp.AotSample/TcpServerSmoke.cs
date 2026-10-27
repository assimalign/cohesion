using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
namespace Assimalign.Cohesion.Database.Tcp.AotSample;

// Linked into the integration tests; no test framework dependency or test-project reference.
internal static class TcpServerSmoke
{
    internal static readonly string[] Models = ["SQL", "Graph", "Documents", "Blob", "KeyValuePair"];

    internal static (DatabaseEngine Engine, IDuplexConnectionListener Listener) Create(int model, Uri endpoint)
    {
        IDuplexConnectionListener? listener = null;
        void Configure<TOptions>(TOptions options) where TOptions : DatabaseServerOptions
        {
            TOptions concrete = options.Listen(endpoint);
            if (!ReferenceEquals(concrete, options)) { throw new InvalidOperationException("Listen changed the options instance."); }
            listener = concrete.Listener;
        }
        DatabaseEngine engine = model switch
        {
            0 => SqlDatabaseEngine.CreateBuilder("tcp-" + Models[model]).AddServer(options => Configure(options)).Build(),
            1 => GraphDatabaseEngine.CreateBuilder("tcp-" + Models[model]).AddServer(options => Configure(options)).Build(),
            2 => DocumentDatabaseEngine.CreateBuilder("tcp-" + Models[model]).AddServer(options => Configure(options)).Build(),
            3 => BlobDatabaseEngine.CreateBuilder("tcp-" + Models[model]).AddServer(options => Configure(options)).Build(),
            4 => KeyValueDatabaseEngine.CreateBuilder("tcp-" + Models[model]).AddServer(options => Configure(options)).Build(),
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };
        return (engine, listener ?? throw new InvalidOperationException("The server builder did not configure its listener."));
    }

    internal static async Task<(IPEndPoint Endpoint, ProtocolMessageType[] Responses)> RunAsync(int model, CancellationToken token)
    {
        // Keep the selected positive endpoint reserved throughout engine/database setup. Resource
        // endpoint URIs require a positive port; release only immediately before the listener bind.
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        var (engine, listener) = Create(model, new Uri($"cohesion-db://127.0.0.1:{port}"));
        await using var owned = engine;
        await engine.CreateDatabaseAsync("app", token);
        var server = engine.Servers.Single();
        reservation.Dispose();
        await server.StartAsync(token);
        var endpoint = (IPEndPoint)listener.EndPoint;
        await using var transport = await new TcpConnectionFactory().ConnectAsync(endpoint, token);
        var family = model switch
        {
            0 => SqlProtocol.Family, 1 => GraphProtocol.Family, 2 => DocumentProtocol.Family,
            3 => BlobProtocol.Family, 4 => KeyValueProtocol.Family,
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };
        await using var channel = new ProtocolChannel(transport.AsStream(), family, leaveOpen: true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "app", "tcp-smoke").Encode());
        var authenticate = await Read();
        await Send(ProtocolMessageType.AuthenticateResponse, []);
        var ready = await Read();
        await Send(ProtocolMessageType.Ping, []);
        var pong = await Read();
        await Send(ProtocolMessageType.Terminate, []);
        return (endpoint, [authenticate.Type, ready.Type, pong.Type]);

        async ValueTask Send(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), token);
            await channel.Writer.FlushAsync(token);
        }
        async ValueTask<ProtocolFrame> Read() => await channel.Reader.ReadFrameAsync(token)
            ?? throw new ProtocolException("The TCP server closed before completing the handshake and Ping.");
    }
}
