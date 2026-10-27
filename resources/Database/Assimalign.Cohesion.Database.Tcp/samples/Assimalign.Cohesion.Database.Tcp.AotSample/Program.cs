using System;
using System.Linq;
using System.Threading;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Tcp.AotSample;
// Sixty-second outer hang guard for the TCP smoke cases and function exchanges; this is not a timing measurement.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
for (int model = 0; model < TcpServerSmoke.Models.Length; model++)
{
    var result = await TcpServerSmoke.RunAsync(model, timeout.Token);
    if (result.Endpoint.Port <= 0 || !result.Responses.SequenceEqual(new[] { ProtocolMessageType.Authenticate, ProtocolMessageType.Ready, ProtocolMessageType.Pong }))
    { throw new InvalidOperationException("A database model failed its TCP handshake/Ping smoke."); }
    Console.WriteLine(TcpServerSmoke.Models[model] + ": TCP handshake and Ping passed.");
}
Console.WriteLine("Database.Tcp NativeAOT: 5/5 model servers passed.");
await KeyValueFunctionSmoke.RunAsync(timeout.Token);
