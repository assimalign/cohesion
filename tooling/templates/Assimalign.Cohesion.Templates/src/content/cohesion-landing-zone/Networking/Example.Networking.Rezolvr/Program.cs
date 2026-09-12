using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting;

// Owns authoritative DNS, forwarding, and durable records for the organization.
// Application-specific records remain commands declared by their owning gateways.
IRezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder(args);

await builder.Build().RunAsync();
