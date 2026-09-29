using System;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);

// The area ApplicationModel packages own the verbs; the gateway build supplies only the manifests.
if (Array.Exists(args, static argument => argument == "--typed-options"))
{
    _ = builder.AddDatabase(
        Manifests.GatewaySmokeDatabase,
        new DatabaseResourceOptions { Storage = { Size = "20Gi" } });
    _ = builder.AddWeb(Manifests.GatewaySmokeWeb, new WebResourceOptions { Replicas = 2 });
}
else
{
    builder.AddDatabase(Manifests.GatewaySmokeDatabase);
    builder.AddWeb(Manifests.GatewaySmokeWeb);
}

if (Array.Exists(args, static argument => argument == "--configure-provider"))
{
    builder.UseGateway(args, static gateways => gateways.JitTest());
}
else
{
    builder.UseGateway(args);
}
await builder.Build().RunAsync();
