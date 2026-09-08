using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = Externals.PlatformDatabase;
builder.AddAllResources();
builder.UseGateway(args);
await builder.Build().RunAsync();
