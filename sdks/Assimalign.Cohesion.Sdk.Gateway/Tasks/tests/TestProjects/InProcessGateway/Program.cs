using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = builder.AddWeb(Manifests.GatewaySmokeWeb);
_ = builder.AddWeb(Manifests.InprocessNamedEntry);
_ = builder.AddWeb(Manifests.InprocessNoncomposable);
builder.UseGateway(args);
IApplication application = builder.Build();
await application.RunAsync();
