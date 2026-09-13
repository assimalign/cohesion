using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>Checks the actual process controller and registered area command clients together.</summary>
[Collection(LocalGatewayConsoleCollection.Name)]
public sealed class AreaCommandLocalTests
{
    /// <summary>Commands wait for running children, preserve source declarations, and are withdrawn on teardown.</summary>
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - LocalGateway: area commands apply after Running and teardown deletes durable declarations")]
    public async Task LocalGateway_ShouldApplyAreaCommandsAndTeardown()
    {
        string root = Path.Combine(FindRepository(), "_out", "31c-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new LocalGatewayOptions
        {
            BaseDirectory = AppContext.BaseDirectory,
            StateDirectory = Path.Combine(root, "state"),
            ExportDirectory = Path.Combine(root, "export"),
            ProbeInterval = TimeSpan.FromMilliseconds(50),
            ProbeTimeout = TimeSpan.FromSeconds(2),
            ReadinessBudget = TimeSpan.FromSeconds(30),
            StopGrace = TimeSpan.FromSeconds(5),
        };
        options.Parameters.Add("credential", "local-command-secret");
        var gateway = new LocalGateway(options);
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", ["--environment", "Development"]).UseGateway(gateway);
        // These transport fixtures use generic Development HTTP manifests, as the existing
        // store gateway tests do. Production area planners are tested separately with HTTPS.
        IApplicationResourceDescriptor identity = builder.AddResource(Manifest(root, "identity", "IdentityHub", "https",
            ["identityhub.add-audience", "identityhub.add-client"], credential: true));
        IApplicationResourceDescriptor resolver = builder.AddResource(Manifest(root, "resolver", "Rezolvr", "admin", ["rezolvr.add-a-record"]));
        IApplicationResourceDescriptor secrets = builder.AddResource(Manifest(root, "secrets", "SecretStore", "api", ["secretstore.add-secret"]));
        IApplicationResourceDescriptor copy = builder.AddResource(Manifest(root, "copy", "SecretStore", "api", ["secretstore.add-secret"], source: "secrets"));
        copy.DependsOn(secrets);
        AddCommand(builder, identity, "identityhub.add-audience", "orders", """{"name":"orders"}""");
        AddCommand(builder, identity, "identityhub.add-client", "worker", """{"clientId":"worker","audiences":["orders"],"credentialSource":"credential"}""");
        AddCommand(builder, resolver, "rezolvr.add-a-record", "api.example", """{"name":"api.example","address":"192.0.2.7","ttlSeconds":300}""");
        AddCommand(builder, secrets, "secretstore.add-secret", "key", """{"path":"key","source":"parameter:credential"}""");
        AddCommand(builder, copy, "secretstore.add-secret", "copied", """{"path":"copied","source":"secrets:key"}""");
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
            IApplicationBuilder peerBuilder = Application.CreateBuilder((ApplicationName)"peer", ["--environment", "Development"])
                .UseGateway(new LocalGateway());
            peerBuilder.AddResource(Manifest(root, "peer-resource", "Rezolvr", "admin", ["rezolvr.add-a-record"]) with { Application = "peer" });
            using var peerKey = new GatewayTrustKey(ECDsa.Create(ECCurve.NamedCurves.nistP256));
            ApplicationExportDocument peerExport = ApplicationExportDocument.Create(peerBuilder.Build().Model, "1", trustKey: peerKey.PublicJwk);
            await gateway.AddTrustedIssuerAsync(model, "peer", peerExport, ["rezolvr.add-a-record"], cancellation.Token);
            gateway.GetTrustedIssuers(model.Name).Single(issuer => issuer.Issuer == "peer").AllowedCommandKinds
                .ShouldBe(["rezolvr.add-a-record"]);
            ResourceEndpoint copiedEndpoint = gateway.ResourceStates.GetObservedEndpoints(copy.Resource.Id).Single(endpoint => endpoint.Name == "api");
            string copiedToken = ((IResourceCommandCredentialProvider)gateway).GetResourceCommandCredential(model.Name, copy.Resource.Name);
            Assimalign.Cohesion.SecretStore.Client.ISecretStoreClient copiedClient = Assimalign.Cohesion.SecretStore.Client.SecretStoreClient.Create(
                new UriBuilder(copiedEndpoint.Scheme, copiedEndpoint.Host!, copiedEndpoint.Port).Uri,
                new Assimalign.Cohesion.SecretStore.Client.ClientCredential(copiedToken));
            Encoding.UTF8.GetString((await copiedClient.GetSecretAsync("copied", cancellation.Token)).Span).ShouldBe("local-command-secret");
            foreach (IApplicationResource resource in model.Resources)
            {
                gateway.ResourceStates.GetState(resource.Id).ShouldBe(ResourceLifecycle.Running);
                gateway.ResourceStates.GetCommandObservations(resource.Id).ShouldNotBeEmpty();
                gateway.ResourceStates.GetCommandObservations(resource.Id).ShouldAllBe(observation => observation.Status == ResourceCommandStatus.Applied);
            }
            foreach (IResourceCommand declaration in model.Commands)
            {
                Encoding.UTF8.GetString(declaration.Payload.Span).ShouldNotContain("local-command-secret", Case.Sensitive);
                Encoding.UTF8.GetString(declaration.Payload.Span).ShouldNotContain("resolvedValue", Case.Sensitive);
            }
            using JsonDocument records = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "resolver", "data", "records.json"), cancellation.Token));
            records.RootElement.GetArrayLength().ShouldBe(1);
            using JsonDocument registry = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "identity", "data", "registry.json"), cancellation.Token));
            registry.RootElement.GetArrayLength().ShouldBe(2);
            await ((IApplicationGateway)gateway).UninstallAsync(model, cancellation.Token);
            using JsonDocument removedRecords = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "resolver", "data", "records.json"), cancellation.Token));
            removedRecords.RootElement.GetArrayLength().ShouldBe(0);
            using JsonDocument removedClients = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "identity", "data", "registry.json"), cancellation.Token));
            removedClients.RootElement.GetArrayLength().ShouldBe(0);
            foreach (IApplicationResource resource in model.Resources)
            {
                gateway.ResourceStates.GetCommandObservations(resource.Id).ShouldBeEmpty();
            }
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AddCommand(IApplicationBuilder builder, IApplicationResourceDescriptor target, string kind, string key, string json)
    {
        using JsonDocument payload = JsonDocument.Parse(json);
        builder.AddCommand(ResourceCommands.Create(kind, key, target.Resource, (ApplicationName)"appa", payload.RootElement,
            GatewayCommandJsonContext.Default.JsonElement));
    }

    private static ResourceManifest Manifest(string root, string name, string kind, string endpoint, string[] commands, bool credential = false, string? source = null) => new()
    {
        Name = name, Application = "appa", Kind = kind, ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
        Artifact = new ResourceManifestArtifact
        {
            Assembly = "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost.dll",
            AppHost = Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)),
        },
        Commands = commands.Select(kind => new ResourceManifestCommand(kind)).ToArray(),
        Endpoints = [new ResourceManifestEndpoint { Name = endpoint, Scheme = "http", Protocol = "tcp", ContainerPort = 8080 }],
        ControlPlane = new ResourceManifestControlPlane { Endpoint = endpoint, Path = "/cohesion/v1" },
        EnvironmentVariables = new Dictionary<string, string> { ["TEST_RESOURCE_AREA"] = kind, ["TEST_COMMAND_DATA"] = Path.Combine(root, name) },
        Mounts = credential ? [new ResourceManifestMount { Name = "credential", ContainerPath = "/credential", Kind = ResourceMountKind.Secret, Source = "parameter:credential" }] : [],
        References = source is null ? [] : [new ResourceManifestReference { Resource = source, Application = "appa", Endpoints = ["api"], Manifest = "Assimalign.Cohesion.Test.ApplicationModel" }],
        Probes = new ResourceManifestProbes { Readiness = new ResourceManifestProbe { Endpoint = endpoint, Http = "/readyz" } },
        Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment, Replicas = 1, RestartPolicy = "Never", StopGraceSeconds = 5 },
    };

    private static string FindRepository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Assimalign.Cohesion.slnx"))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
