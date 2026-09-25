using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);

builder.RemoteReference(
    Externals.PlatformConfigurationStore,
    remote => remote.Endpoint("api", "https://localhost:18443"));
builder.RemoteReference(Externals.IdentityHub, remote => { });

builder.AddAppBDatabase();
builder.AddAppBApi();
builder.AddAppBSpa();
// AppBSecretStore stays referenced and generated; this sample realizes the Database/API/SPA subset.
builder.UseGateway(args);

await builder.Build().RunAsync();
