using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddAllResources();
builder.UseGateway(args, gateways =>
    gateways.InProcess(options =>
    {
        options.ProbeInterval = TimeSpan.FromMilliseconds(100);
        options.ProbeTimeout = TimeSpan.FromSeconds(2);
        options.InitialRestartBackoff = TimeSpan.Zero;
        options.MaximumRestartBackoff = TimeSpan.Zero;
    }));
IApplication application = builder.Build();

if (!Array.Exists(args, static argument => argument == "--smoke"))
{
    await application.RunAsync();
    return;
}

using var smoke = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Task run = application.RunAsync(smoke.Token);
while (GatewaySmoke.Web.ProcessMarker.StartedCount < 2
    || !GatewaySmoke.Web.ProcessMarker.RestartReadinessObserved
    || GatewaySmoke.Database.ProcessMarker.StartedProcessId == 0)
{
    Task delay = Task.Delay(TimeSpan.FromMilliseconds(50), smoke.Token);
    Task completed = await Task.WhenAny(run, delay);
    if (ReferenceEquals(completed, run))
    {
        await run;
        throw new InvalidOperationException(
            "The in-process gateway stopped before the Web member restarted, returned to readiness, "
            + "and the Database member started.");
    }
    await delay;
}

int gatewayProcess = Environment.ProcessId;
if (GatewaySmoke.Web.ProcessMarker.StartedProcessId != gatewayProcess
    || GatewaySmoke.Database.ProcessMarker.StartedProcessId != gatewayProcess)
{
    throw new InvalidOperationException("The Gateway, Web, and Database members did not share one process.");
}

smoke.Cancel();
await run;
