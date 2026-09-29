using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Each referenced resource project contributes a generated Manifests member; the gateway names what it
// composes with the area's own verb, and passes options such as new WebResourceOptions { Replicas = 2 }.
builder.AddDatabase(Manifests.AcmeDatabase);
builder.AddWeb(Manifests.AcmeApi);
builder.UseGateway(args);

await builder.Build().RunAsync();
