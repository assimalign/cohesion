using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.KeyValuePair;

// This is a hang guard, not a timing assertion. The parent uses an explicit READY/STOP handshake.
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
using Socket keyValueReservation = ReserveEndpoint();
using Socket graphReservation = ReserveEndpoint();
using Socket blobReservation = ReserveEndpoint();
int keyValuePort = ((IPEndPoint)keyValueReservation.LocalEndPoint!).Port;
int graphPort = ((IPEndPoint)graphReservation.LocalEndPoint!).Port;
int blobPort = ((IPEndPoint)blobReservation.LocalEndPoint!).Port;
Uri keyValueEndpoint = Uri.CreateEndpoint("cohesion-db", "127.0.0.1", keyValuePort);
Uri graphEndpoint = Uri.CreateEndpoint("cohesion-db", "127.0.0.1", graphPort);
Uri blobEndpoint = Uri.CreateEndpoint("cohesion-db", "127.0.0.1", blobPort);

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
builder.AddKeyValue("tcp-key-value", engine =>
    engine.AddDatabase("app").AddServer(server => server.Listen(keyValueEndpoint)));
builder.AddGraph("tcp-graph", engine =>
    engine.AddDatabase("app").AddServer(server => server.Listen(graphEndpoint)));
builder.AddBlob("tcp-blob", engine =>
    engine.AddDatabase("app").AddServer(server => server.Listen(blobEndpoint)));

await using DatabaseApplication application = builder.Build(lifetime.Token);
// Resource endpoint URIs need positive ports. Hold their BCL reservations through provisioning,
// releasing them immediately before the real listeners bind, as the Database.Tcp smoke does.
keyValueReservation.Dispose();
graphReservation.Dispose();
blobReservation.Dispose();
IDatabaseApplication host = application;
await host.StartAsync(lifetime.Token);
try
{
    Console.WriteLine(string.Join('|', "READY", keyValuePort.ToString(CultureInfo.InvariantCulture),
        graphPort.ToString(CultureInfo.InvariantCulture), blobPort.ToString(CultureInfo.InvariantCulture)));
    await Console.Out.FlushAsync(lifetime.Token);
    string? command = await Console.In.ReadLineAsync(lifetime.Token);
    if (!string.Equals(command, "STOP", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The TCP SDK fixture expected its STOP command.");
    }
}
finally
{
    // Independent cleanup hang guard: an expired parent/lifetime token must not skip shutdown.
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await host.StopAsync(shutdown.Token);
}
Console.WriteLine("STOPPED");

static Socket ReserveEndpoint()
{
    var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    try
    {
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return reservation;
    }
    catch (SocketException)
    {
        reservation.Dispose();
        throw;
    }
}
