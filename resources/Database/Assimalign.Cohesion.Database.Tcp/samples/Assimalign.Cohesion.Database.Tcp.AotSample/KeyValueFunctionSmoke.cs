using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using KeyValueWire = Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.Tcp.AotSample;

/// <summary>Exercises typed delegates and protected-core callbacks in a native server over the existing Execute frames.</summary>
internal static class KeyValueFunctionSmoke
{
    internal static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        IDuplexConnectionListener? listener = null;
        var builder = KeyValueDatabaseEngine.CreateBuilder("kv-functions-aot");
        builder.Functions.Add(KeyValueFunction.Create<long, long>("increment", static value => checked(value + 1)));
        builder.Functions.Add(new ManualAnswer());
        builder.AddServer(options =>
        {
            options.Listen(new Uri($"cohesion-db://127.0.0.1:{port}"));
            listener = options.Listener;
        });
        await using var engine = builder.Build();
        await engine.CreateDatabaseAsync("app", cancellationToken);
        reservation.Dispose();
        var server = engine.Servers.Single();
        await server.StartAsync(cancellationToken);
        await using var transport = await new TcpConnectionFactory().ConnectAsync(listener!.EndPoint, cancellationToken);
        await using var channel = new ProtocolChannel(transport.AsStream(), KeyValueWire.KeyValueProtocol.Family, leaveOpen: true);
        await SendAsync(ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "app", "kv-functions-aot").Encode());
        await ExpectAsync(ProtocolMessageType.Authenticate);
        await SendAsync(ProtocolMessageType.AuthenticateResponse, []);
        await ExpectAsync(ProtocolMessageType.Ready);
        var parameter = new DatabaseKeyWriter();
        DatabaseValueCodec.Append(parameter, 41L);
        await CallAsync("CALL increment @p0", new Dictionary<string, byte[]> { ["p0"] = parameter.ToArray() });
        await CallAsync("CALL manual_answer", new Dictionary<string, byte[]>());
        Console.WriteLine("Database.Tcp NativeAOT: key-value delegate and protected-core function calls passed over Execute.");

        async Task CallAsync(string command, IReadOnlyDictionary<string, byte[]> parameters)
        {
            await SendAsync((ProtocolMessageType)KeyValueWire.KeyValueProtocolMessageType.Execute,
                new KeyValueWire.ProtocolExecuteMessage(command, parameters).Encode());
            bool header = false;
            await ExpectAsync((ProtocolMessageType)KeyValueWire.KeyValueProtocolMessageType.ResultHeader, payload =>
            {
                var columns = KeyValueWire.ProtocolResultHeaderMessage.Decode(payload.Span).Columns;
                Check(columns.Count == 1 && columns[0].Name == "value" && columns[0].Type == (byte)DatabaseType.Int64);
                header = true;
            });
            await ExpectAsync((ProtocolMessageType)KeyValueWire.KeyValueProtocolMessageType.ResultRow, payload =>
            {
                var reader = new DatabaseKeyReader(payload.Span);
                Check(header && Equals(DatabaseValueCodec.Read(ref reader), 42L) && reader.IsAtEnd);
            });
            await ExpectAsync((ProtocolMessageType)KeyValueWire.KeyValueProtocolMessageType.ResultComplete,
                payload => Check(KeyValueWire.ProtocolResultCompleteMessage.Decode(payload.Span).AffectedCount == -1));
        }

        async Task SendAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), cancellationToken);
            await channel.Writer.FlushAsync(cancellationToken);
        }

        async Task ExpectAsync(ProtocolMessageType type, Action<ReadOnlyMemory<byte>>? inspect = null)
        {
            Check(await channel.Reader.ReadBorrowedFrameAsync(frame =>
            {
                Check(frame.Type == type);
                inspect?.Invoke(frame.Payload);
            }, cancellationToken));
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) { throw new InvalidOperationException("Key-value native function dispatch check failed."); }
    }

    private sealed class ManualAnswer : KeyValueFunction
    {
        internal ManualAnswer() : base("manual_answer", [], DatabaseType.Int64) { }

        protected override object? InvokeCore(scoped in KeyValueArguments arguments)
        {
            arguments.Context.CancellationToken.ThrowIfCancellationRequested();
            return 42L;
        }
    }
}
