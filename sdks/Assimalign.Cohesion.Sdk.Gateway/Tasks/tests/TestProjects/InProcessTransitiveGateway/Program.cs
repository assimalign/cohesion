using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = builder.AddWeb(Manifests.GatewaySmokeWeb);
// Database reaches this gateway only through the Web project's closure, so the SDK injects no
// Database ApplicationModel; the untyped verb composes its manifest.
_ = builder.AddResource(Manifests.GatewaySmokeDatabase);
builder.UseGateway(args);
IApplication application = builder.Build();
await application.RunAsync();
