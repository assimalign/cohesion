using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting;

// Owns AppC secrets and leaf certificates under the platform trust hierarchy.
SecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);

await using SecretStoreApplication application = builder.Build();
await application.RunAsync();
