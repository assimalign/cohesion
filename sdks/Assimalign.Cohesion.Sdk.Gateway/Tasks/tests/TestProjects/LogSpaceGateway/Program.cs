using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Compiles only when the SDK injected the LogSpace ApplicationModel for the referenced project.
ILogSpaceResourceDescriptor logs = builder.AddLogSpace(Manifests.TypedLogs);
builder.UseGateway(args);
await builder.Build().RunAsync();
