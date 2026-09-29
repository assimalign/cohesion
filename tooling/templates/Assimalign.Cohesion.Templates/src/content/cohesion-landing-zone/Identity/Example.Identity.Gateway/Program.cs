using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);

// Command-line and environment bindings override this standalone-development fallback.
builder.RemoteReference(
    Externals.PlatformSecretStore,
    remote => remote.Endpoint("api", "https://localhost:18444"));
// The enabled resource project provides its area-owned manifest and default control plane. Its TLS
// bundle and signing keys are the gateway parameters identity-hub-tls and identity-signing-keys:
// cross-application store sources are a documented follow-up, so they cannot read the Platform store.
builder.AddIdentityHub(Manifests.IdentityHub);
builder.UseGateway(args);

await builder.Build().RunAsync();
