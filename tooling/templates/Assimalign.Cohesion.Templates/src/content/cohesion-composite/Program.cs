using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Compose each CohesionResourceReference member with its generated verb, for example
// builder.AddApi() for a referenced Example.Api project; the gateway names what it composes.
builder.UseGateway(args);

await builder.Build().RunAsync();
