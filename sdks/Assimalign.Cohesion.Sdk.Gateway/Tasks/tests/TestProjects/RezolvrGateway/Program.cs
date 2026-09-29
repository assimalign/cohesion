using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Compiles only when the SDK injected the Rezolvr ApplicationModel for the referenced project.
IRezolvrResourceDescriptor rezolvr = builder.AddRezolvr(Manifests.TypedRezolvr);
builder.UseGateway(args);
await builder.Build().RunAsync();
