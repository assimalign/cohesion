using System;
using System.Threading;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Compose each CohesionResourceReference member with its generated verb, for example
// builder.AddApi() for a referenced Example.Api project; the gateway names what it composes.
builder.UseGateway(args);

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler stop = (_, signal) =>
{
    signal.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += stop;
try
{
    await builder.Build().RunAsync(shutdown.Token);
}
finally
{
    Console.CancelKeyPress -= stop;
}
