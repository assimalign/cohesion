using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Hand-written verbs over the generated manifests: the in-process bindings come only from
// Gateway.CreateBuilder's registration by manifest identity. gateway-smoke-web goes through the
// Web area verb; gateway-smoke-database reaches this gateway only through the Web project's
// closure, so its ApplicationModel is not injected and it is added on the untyped path.
_ = builder.AddWeb(Manifests.GatewaySmokeWeb);
_ = builder.AddResource(Manifests.GatewaySmokeDatabase);
builder.UseGateway(args);
IApplication application = builder.Build();
await application.RunAsync();
