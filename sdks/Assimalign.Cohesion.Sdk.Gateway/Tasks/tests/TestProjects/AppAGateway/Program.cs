using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = Externals.PlatformConfigurationStore;
builder.AddAppAWeb();
builder.UseGateway(args);
await builder.Build().RunAsync();
