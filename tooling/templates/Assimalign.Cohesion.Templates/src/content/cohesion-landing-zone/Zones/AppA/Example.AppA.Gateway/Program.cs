using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);

// AppA consumes this resource across the platform boundary, so the SDK generates
// Externals.PlatformConfigurationStore, which this gateway binds rather than realizes.
builder.RemoteReference(
    Externals.PlatformConfigurationStore,
    remote => remote.Endpoint("api", "https://localhost:18443"));
builder.RemoteReference(Externals.IdentityHub, remote => { });

builder.AddDatabase(Manifests.AppADatabase);
builder.AddWeb(Manifests.AppAApi);
builder.AddWeb(Manifests.AppASpa);
// Manifests.AppASecretStore stays referenced and generated; this sample realizes the Database/API/SPA
// subset. Realizing the store also means referencing Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration
// and registering it here, because nothing is registered by convention.
builder.UseGateway(args);

await builder.Build().RunAsync();
