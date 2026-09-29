using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
IDatabaseResourceDescriptor database = builder.AddDatabase(Manifests.GatewaySmokeDatabase);
database.AddDatabase("inventory").AddPrincipal("inventory", "reader");

IConfigurationStoreResourceDescriptor configuration = builder.AddConfigurationStore(Manifests.CommandConfiguration);
using JsonDocument value = JsonDocument.Parse("\"enabled\"");
configuration.SetValue("inventory", "mode", value.RootElement).RemoveValue("inventory", "legacy");

ISecretStoreResourceDescriptor secrets = builder.AddSecretStore(Manifests.CommandSecrets)
    .AddSecret("/cohesion/mounts/tls", "parameter:tls")
    .IssueCertificate("api", subject: "CN=api");

// The SecretStore manifest marks secretstore.add-secret RequiresInputResolver, so Build() fails unless
// the gateway registers a resolver for it. A real gateway calls builder.UseSecretStore(secrets) from
// SecretStore.ApplicationModel.Orchestration. The test drives this gateway only in --mode=describe,
// which never delivers, so it registers a pass-through resolver instead and keeps its reference
// closure free of the SecretStore client that package brings.
builder.Providers.CommandInputs.Add(new DescribeOnlyCommandInputResolver("secretstore.add-secret"));

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

/// <summary>
/// Satisfies a command's input-resolver requirement for a describe-only gateway by returning the
/// declared payload unchanged; it never runs, because describe mode delivers nothing.
/// </summary>
internal sealed class DescribeOnlyCommandInputResolver : IResourceCommandInputResolver
{
    public DescribeOnlyCommandInputResolver(string commandKind)
    {
        CommandKind = commandKind;
    }

    public string CommandKind { get; }

    public ValueTask<ReadOnlyMemory<byte>> ResolveAsync(
        ResourceCommandInput declared,
        IResourceSourceResolver sources,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(declared.Payload);
}
