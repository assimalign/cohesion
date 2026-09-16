using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Hosting;
using Assimalign.Cohesion.Hosting;

// Owns durable configuration, platform namespaces, seed data, and promotion policy.
// Zone namespaces remain commands declared by their owning gateways.
ConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder(args);

await using ConfigurationStoreApplication application = builder.Build();
await application.RunAsync();
