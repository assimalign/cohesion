using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting;

IIdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);

await builder.Build().RunAsync();
