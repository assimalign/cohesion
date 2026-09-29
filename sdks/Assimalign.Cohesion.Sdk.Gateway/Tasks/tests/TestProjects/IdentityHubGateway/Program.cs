using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Compiles only when the SDK injected the IdentityHub ApplicationModel for the referenced project.
IIdentityHubResourceDescriptor identity = builder.AddIdentityHub(Manifests.TypedIdentity);
builder.UseGateway(args);
await builder.Build().RunAsync();
