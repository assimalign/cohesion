using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting;

IdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);

await using IdentityHubApplication application = builder.Build();
await application.RunAsync();
