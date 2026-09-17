using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ConfigurationStore.ApplicationModel;
using Assimalign.Cohesion.Database.ApplicationModel;
using Assimalign.Cohesion.SecretStore.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
IDatabaseResourceDescriptor database = builder.AddGatewaySmokeDatabase();
database.AddDatabase("inventory").AddPrincipal("inventory", "reader");

IConfigurationStoreResourceDescriptor configuration = builder.AddCommandConfiguration();
using JsonDocument value = JsonDocument.Parse("\"enabled\"");
configuration.SetValue("inventory", "mode", value.RootElement).RemoveValue("inventory", "legacy");

ISecretStoreResourceDescriptor secrets = builder.AddCommandSecrets()
    .AddSecret("/cohesion/mounts/tls", "parameter:tls")
    .IssueCertificate("api", subject: "CN=api");

builder.UseGateway(args);
var application = builder.Build();
AssertCommandParity(application.Model, "Database", DatabaseResourceControlPlane.Create().AcceptedCommandKinds);
AssertCommandParity(application.Model, "ConfigurationStore", ConfigurationStoreResourceControlPlane.Create().AcceptedCommandKinds);
// Trust grants are control-plane bootstrap commands, not manifest-declared SecretStore commands.
AssertCommandParity(application.Model, "SecretStore",
    SecretStoreResourceControlPlane.Create().AcceptedCommandKinds
        .Where(static kind => kind != "cohesion.trust.add"));
await application.RunAsync();

static void AssertCommandParity(IApplicationModel model, string kind, IEnumerable<string> acceptedCommands)
{
    string[] published = model.Manifests.Single(manifest => manifest.Kind == kind).Commands
        .Select(command => command.Kind).Order(StringComparer.Ordinal).ToArray();
    string[] accepted = acceptedCommands.Order(StringComparer.Ordinal).ToArray();
    if (!published.SequenceEqual(accepted, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"{kind} manifest commands [{string.Join(", ", published)}] differ from its default control plane [{string.Join(", ", accepted)}].");
    }
}
