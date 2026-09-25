using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// The enabled resource projects provide their area-owned manifests and default control planes.
builder.AddPlatformSecretStore();
builder.AddPlatformConfigurationStore();
builder.AddPlatformLogSpace();
builder.UseGateway(args);

await builder.Build().RunAsync();
