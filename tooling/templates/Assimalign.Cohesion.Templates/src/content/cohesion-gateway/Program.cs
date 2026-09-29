using System;
using System.Threading;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Compose each CohesionResourceReference member with its area's verb over the generated Manifests
// member, for example builder.AddWeb(Manifests.ExampleApi, new WebResourceOptions { Replicas = 2 })
// for a referenced Example.Api project, or builder.AddResource(Manifests.ExampleWorker) for a kind
// without an ApplicationModel package. Providers are explicit too: reference a store's
// Assimalign.Cohesion.<Area>.ApplicationModel.Orchestration package and register it here, for example
// builder.UseSecretStore(secrets).AsCertificateAuthority().AsTrustStore().
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
