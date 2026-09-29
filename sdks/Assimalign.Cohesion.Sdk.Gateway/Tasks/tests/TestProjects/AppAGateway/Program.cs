using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
_ = Externals.PlatformConfigurationStore;
builder.AddWeb(Manifests.AppAWeb);
builder.UseGateway(args);
await builder.Build().RunAsync();
