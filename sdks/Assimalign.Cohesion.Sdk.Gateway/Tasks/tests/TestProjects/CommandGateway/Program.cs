using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
IDatabaseResourceDescriptor database = builder.AddDatabase(Manifests.GatewaySmokeDatabase);
database.AddDatabase("inventory").AddPrincipal("inventory", "reader");
builder.AddConfigurationStore(Manifests.CommandConfiguration);
builder.AddSecretStore(Manifests.CommandSecrets);

builder.UseGateway(args);
var application = builder.Build();
AssertRuntimeBinding(application.Model, "ConfigurationStore");
AssertRuntimeBinding(application.Model, "SecretStore");
await application.RunAsync();

static void AssertRuntimeBinding(IApplicationModel model, string kind)
{
    ResourceManifest manifest = model.Manifests.Single(candidate => candidate.Kind == kind);
    if (manifest.Schema != ResourceManifest.SchemaV2 ||
        manifest.Runtime?.GetProperty("management").GetProperty("protocol").GetString() != "cohesion/resource-state/v1")
    {
        throw new InvalidOperationException("A new resource manifest must publish its runtime management binding.");
    }
}
