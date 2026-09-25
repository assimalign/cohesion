using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);

// Command-line and environment bindings override these standalone-development fallbacks.
builder.RemoteReference(
    Externals.PlatformSecretStore,
    remote => remote.Endpoint("api", "https://localhost:18444"));
builder.RemoteReference(
    Externals.PlatformConfigurationStore,
    remote => remote.Endpoint("api", "https://localhost:18443"));
// The enabled resource projects provide their area-owned manifests and default control planes.
builder.AddNetworkingRezolvr();
builder.AddNetworkingVpnGateway();
builder.UseGateway(args);

await builder.Build().RunAsync();
