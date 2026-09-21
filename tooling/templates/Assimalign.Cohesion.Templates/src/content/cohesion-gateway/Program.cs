using System;
using System.Threading;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddAllResources();
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
