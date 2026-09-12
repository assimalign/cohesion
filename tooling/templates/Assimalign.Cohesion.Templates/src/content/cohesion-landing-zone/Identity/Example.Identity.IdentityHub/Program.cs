using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting;

// Owns the organization's directory, token service, OIDC issuer, and user-flow pipeline.
// Zone audiences and clients remain declarations of the consuming zone gateways.
IIdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);

await builder.Build().RunAsync();
