using System;
using System.Threading;

using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

// The in-process platform runs each resource's program on a thread of this process. It runs only the
// programs registered here: reference a resource's project and register its entry point, for example
// infrastructure.AddProgram("Web", "acme-api", Acme.Api.Program.MainAsync). README.md describes the
// other platforms.
await using var infrastructure = new InProcessInfrastructure();

ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("acme").UseInProcess(infrastructure);

// The model: one AddResource(provider, name) per resource, with its dependencies and settings.
builder.AddResource("Web", "acme-api")
    .WithSetting("Customers:PageSize", "25");

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
