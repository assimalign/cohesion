using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// The area verb over the generated manifest instead of the generated AddGatewaySmokeWeb() verb:
// the in-process binding for gateway-smoke-web must come from Gateway.CreateBuilder's manifest
// registration, which is what a third-party application model's verb relies on too.
_ = builder.AddWeb(Manifests.GatewaySmokeWeb);
_ = builder.AddGatewaySmokeDatabase();
builder.UseGateway(args);
IApplication application = builder.Build();
await application.RunAsync();
