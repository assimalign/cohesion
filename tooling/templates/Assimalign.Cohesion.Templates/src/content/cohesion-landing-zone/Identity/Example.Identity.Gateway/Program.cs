using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);

// Command-line and environment bindings override this standalone-development fallback.
builder.RemoteReference(
    Externals.PlatformSecretStore,
    remote => remote.Endpoint("api", "https://localhost:18444"));
// The enabled resource project provides its area-owned manifest and default control plane.
builder.AddIdentityHub();
builder.UseGateway(args);

await builder.Build().RunAsync();
