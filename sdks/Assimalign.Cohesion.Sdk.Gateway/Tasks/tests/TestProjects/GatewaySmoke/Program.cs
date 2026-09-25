using System;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = Manifests.GatewaySmokeDatabase;
_ = Manifests.GatewaySmokeWeb;

if (Array.Exists(args, static argument => argument == "--typed-verbs"))
{
    _ = builder.AddGatewaySmokeDatabase(options => options.Storage.Size = "20Gi");
    _ = builder.AddGatewaySmokeWeb(options => options.Replicas = 2);
}
else
{
    builder.AddGatewaySmokeDatabase();
    builder.AddGatewaySmokeWeb();
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
