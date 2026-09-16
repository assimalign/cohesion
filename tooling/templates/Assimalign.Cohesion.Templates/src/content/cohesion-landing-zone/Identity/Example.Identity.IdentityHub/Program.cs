using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting;

// Owns the organization's directory, token service, OIDC issuer, and user-flow pipeline.
// Zone audiences and clients remain declarations of the consuming zone gateways.
IdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);

await using IdentityHubApplication application = builder.Build();
await application.RunAsync();
