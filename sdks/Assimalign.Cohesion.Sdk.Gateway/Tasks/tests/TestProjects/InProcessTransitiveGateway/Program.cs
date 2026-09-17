using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = builder.AddGatewaySmokeWeb();
_ = builder.AddGatewaySmokeDatabase();
builder.UseGateway(args);
IApplication application = builder.Build();
await application.RunAsync();
