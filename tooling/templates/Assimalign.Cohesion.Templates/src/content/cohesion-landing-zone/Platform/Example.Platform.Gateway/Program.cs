using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// The enabled resource projects provide their area-owned manifest and default control plane.
builder.AddAllResources();
builder.UseGateway(args);

await builder.Build().RunAsync();
