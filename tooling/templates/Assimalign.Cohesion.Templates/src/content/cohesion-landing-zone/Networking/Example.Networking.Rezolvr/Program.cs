using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting;

// Owns authoritative DNS, forwarding, and durable records for the organization.
// Application-specific records remain commands declared by their owning gateways.
RezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder(args);

await using RezolvrApplication application = builder.Build();
await application.RunAsync();
