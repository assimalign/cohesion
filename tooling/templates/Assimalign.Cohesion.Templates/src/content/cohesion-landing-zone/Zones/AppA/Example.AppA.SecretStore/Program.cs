using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting;

// Owns AppA secrets and leaf certificates under the platform trust hierarchy.
ISecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);

await builder.Build().RunAsync();
