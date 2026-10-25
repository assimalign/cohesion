using System;
using System.Linq;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
IDatabaseResourceDescriptor database = builder.AddDatabase(Manifests.GatewaySmokeDatabase);
database.AddDatabase("inventory").AddPrincipal("inventory", "reader");
builder.AddConfigurationStore(Manifests.CommandConfiguration);
builder.AddSecretStore(Manifests.CommandSecrets);

builder.UseGateway(args);
var application = builder.Build();
AssertResourceV2(application.Model, "ConfigurationStore");
AssertResourceV2(application.Model, "SecretStore");
await application.RunAsync();

static void AssertResourceV2(IApplicationModel model, string kind)
{
    ResourceManifest manifest = model.Manifests.Single(candidate => candidate.Kind == kind);
    if (manifest.Schema != ResourceManifest.SchemaV2)
    {
        throw new InvalidOperationException($"The {kind} manifest must use {ResourceManifest.SchemaV2}.");
    }
}
