using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ResourceCommandTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: Pass application transport trust only to HTTPS targets")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartAsync_Commands_ShouldPassApplicationTrustToClient(bool https)
    {
        // Arrange
        string root = Path.Combine(AppContext.BaseDirectory, "cmd-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var client = new RecordingCommandClient(events);
        var options = new ApplicationGatewayOptions { ExportDirectory = root };
        options.CommandClients.Clear();
        options.CommandClients.Add(client);
        string? pem = https ? new GatewayCertificateAuthority(Path.Combine(root, "appa"), "appa").Issue("db-admin", []) : null;
        var gateway = new TestGateway(state, [new CommandController(events, https ? "https" : "http")], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor target = AddCommandTarget(builder);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

            // Assert
            if (https)
            {
                RemoteCertificateValidationCallback validator = client.Validator.ShouldNotBeNull();
                using X509Certificate2 leaf = X509Certificate2.CreateFromPem(pem!);
                string unrelatedPem = new GatewayCertificateAuthority(Path.Combine(root, "unrelated"), "unrelated").Issue("db-admin", []);
                using X509Certificate2 unrelated = X509Certificate2.CreateFromPem(unrelatedPem);
                validator(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();
                validator(this, unrelated, null, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();
                validator(this, leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();
            }
            else
            {
                client.Validator.ShouldBeNull();
            }
            state.GetCommandObservations(target.Resource.Id).Single().Status.ShouldBe(ResourceCommandStatus.Applied);
            await ((IApplicationGateway)gateway).UninstallAsync(model, cancellation.Token);
            (client.Validator is not null).ShouldBe(https);
            state.GetCommandObservations(target.Resource.Id).ShouldBeEmpty();
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: Apply after Running before dependents and remove on teardown")]
    public async Task StartAsync_Commands_ShouldApplyObserveAndTeardown()
    {
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var client = new RecordingCommandClient(events);
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear();
        options.CommandClients.Add(client);
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor database = AddCommandTarget(builder);
        builder.AddResource(Manifest("api")).DependsOn(database);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
        await ((IApplicationGateway)gateway).ReconcileAsync(model, cancellation.Token);

        events.Take(3).ShouldBe(["reconcile:db", "apply:orders", "reconcile:api"]);
        client.ApplyCount.ShouldBe(1);
        client.Address!.AbsolutePath.ShouldBe("/custom/control");
        client.Token.ShouldNotBeNullOrWhiteSpace();
        client.Command!.Owner.ShouldBe("appa");
        state.GetCommandObservations(database.Resource.Id).Single().Status.ShouldBe(ResourceCommandStatus.Applied);
        gateway.LastExport!.Commands.Single().Id.ShouldBe(model.Commands.Single().Id);
        using var export = new MemoryStream();
        gateway.LastExport.Save(export);
        export.Position = 0;
        ApplicationExportDocument.Load(export).Commands.Single().Status.ShouldBe(ResourceCommandStatus.Applied);

        await ((IApplicationGateway)gateway).UninstallAsync(model, cancellation.Token);
        events.TakeLast(3).ShouldBe(["delete:api", "remove:orders", "delete:db"]);
        state.GetCommandObservations(database.Resource.Id).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: Preserve provider detail and gate only required declarations")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_RejectedCommand_ShouldObserveDetailAndOptionality(bool optional)
    {
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var client = new RecordingCommandClient(events)
        {
            Result = new(ResourceCommandStatus.Rejected, "database principal runtime seam is unavailable"),
        };
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear(); options.CommandClients.Add(client);
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor target = AddCommandTarget(builder, optional);
        IApplicationResourceDescriptor dependent = builder.AddResource(Manifest("api")).DependsOn(target);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        if (optional)
        {
            await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
        }
        else
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token)))
                .Message.ShouldContain(client.Result.Detail);
        }

        ResourceCommandObservation observation = state.GetCommandObservations(target.Resource.Id).Single();
        observation.Status.ShouldBe(ResourceCommandStatus.Rejected);
        observation.Detail.ShouldBe(client.Result.Detail);
        state.GetState(dependent.Resource.Id).ShouldBe(optional ? ResourceLifecycle.Running : ResourceLifecycle.Blocked);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: An exact-kind client wins over a catch-all client registered before it")]
    public async Task StartAsync_ExactAndCatchAllClients_ShouldPreferExactKind()
    {
        // Arrange
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var catchAll = new RecordingCommandClient(events) { ResourceKind = IGatewayResourceCommandClient.AnyKind };
        var exact = new RecordingCommandClient(events) { ResourceKind = "Database" };
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear();
        options.CommandClients.Add(catchAll);
        options.CommandClients.Add(exact);
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor target = AddCommandTarget(builder);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        exact.ApplyCount.ShouldBe(1);
        catchAll.ApplyCount.ShouldBe(0);
        state.GetCommandObservations(target.Resource.Id).Single().Status.ShouldBe(ResourceCommandStatus.Applied);
        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: A catch-all client serves kinds without an exact registration")]
    public async Task StartAsync_OnlyCatchAllMatches_ShouldDeliverThroughCatchAll()
    {
        // Arrange
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var otherKind = new RecordingCommandClient(events) { ResourceKind = "Web" };
        var catchAll = new RecordingCommandClient(events) { ResourceKind = IGatewayResourceCommandClient.AnyKind };
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear();
        options.CommandClients.Add(otherKind);
        options.CommandClients.Add(catchAll);
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor target = AddCommandTarget(builder);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        catchAll.ApplyCount.ShouldBe(1);
        otherKind.ApplyCount.ShouldBe(0);
        catchAll.Address!.AbsolutePath.ShouldBe("/custom/control");
        state.GetCommandObservations(target.Resource.Id).Single().Status.ShouldBe(ResourceCommandStatus.Applied);
        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: No matching client rejects the command with the kind named")]
    public async Task StartAsync_NoMatchingClient_ShouldRejectNamingKind()
    {
        // Arrange
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear();
        options.CommandClients.Add(new RecordingCommandClient(events) { ResourceKind = "Web" });
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor target = AddCommandTarget(builder, optional: true);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

        // Assert
        ResourceCommandObservation observation = state.GetCommandObservations(target.Resource.Id).Single();
        observation.Status.ShouldBe(ResourceCommandStatus.Rejected);
        observation.Detail.ShouldBe("No command client is registered for resource kind 'Database'.");
        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: Direct delivery uses registered control plane without a client")]
    public async Task StartAsync_RegisteredControlPlane_ShouldApplyDirectly()
    {
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        IResourceControlPlane plane = ResourceControlPlane.Create(["database.add-database"]);
        plane.RegisterCommandHandler(new RecordingCommandHandler(events));
        var options = new ApplicationGatewayOptions(); options.CommandClients.Clear();
        var gateway = new TestGateway(state, [new CommandController(events)], options: options)
        {
            DirectControlPlane = (_, _) => plane,
        };
        IApplicationBuilder builder = CreateBuilder(gateway);
        AddCommandTarget(builder);
        IApplicationModel model = builder.Build().Model;

        await ((IApplicationGateway)gateway).StartAsync(model, CancellationToken.None);
        await ((IApplicationGateway)gateway).UninstallAsync(model, CancellationToken.None);

        events.ShouldContain("direct:appa");
        events.ShouldContain("direct-remove:appa");
    }

    private static IApplicationBuilder CreateBuilder(IApplicationGateway gateway) =>
        Application.CreateBuilder(ApplicationName.Parse("appa"), ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: Reapply removes a withdrawn declaration before reconciling")]
    public async Task ReconcileAsync_WithdrawnDeclaration_ShouldRemoveOwnedCommand()
    {
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var client = new RecordingCommandClient(events);
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear(); options.CommandClients.Add(client);
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder before = CreateBuilder(gateway);
        AddCommandTarget(before);
        IApplicationModel initial = before.Build().Model;
        IApplicationBuilder after = CreateBuilder(gateway);
        after.AddResource(Manifest("db"));
        IApplicationModel replacement = after.Build().Model;

        await ((IApplicationGateway)gateway).StartAsync(initial, CancellationToken.None);
        events.Clear();
        await ((IApplicationGateway)gateway).ReconcileAsync(replacement, CancellationToken.None);

        events.ShouldBe(["remove:orders", "reconcile:db"]);
        state.GetCommandObservations(initial.Resources[0].Id).ShouldBeEmpty();
        gateway.LastExport!.Commands.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: In-set declarations share the realized target and refuse another owner")]
    public async Task StartAsync_InSetOwners_ShouldRefuseConflictingCommandId()
    {
        var events = new List<string>();
        IResourceControlPlane plane = ResourceControlPlane.Create(["database.add-database"]);
        plane.RegisterCommandHandler(new RecordingCommandHandler(events));
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [new CommandController(events)])
        {
            DirectControlPlane = (_, _) => plane,
        };
        IApplicationBuilder provider = CreateBuilder(gateway);
        ResourceManifest manifest = Manifest("db");
        provider.AddResource(manifest);
        IApplicationModel ownerModel = provider.Build().Model;
        IApplicationModel first = Claim("claim-one");
        IApplicationModel second = Claim("claim-two");

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            ((IMultiModelApplicationGateway)gateway).StartAsync([ownerModel, first, second], CancellationToken.None));

        failure.Message.ShouldContain("different command or owner");
        events.Count(entry => entry.StartsWith("direct:", StringComparison.Ordinal)).ShouldBe(1);

        IApplicationModel Claim(string name)
        {
            IApplicationBuilder builder = Application.CreateBuilder(ApplicationName.Parse(name), ["--environment", AppEnvironment.Keys.Local])
                .UseGateway(gateway);
            builder.AddResource(Manifest("worker", name));
            IApplicationResourceDescriptor external = builder.RemoteReference(
                new ExternalResourceDeclaration("db", "appa", ["admin"], optional: false, manifest: manifest),
                remote => remote.Endpoint("admin", "http://127.0.0.1:12345"));
            using JsonDocument payload = JsonDocument.Parse("{\"name\":\"orders\"}");
            builder.AddCommand(ResourceCommands.Create("database.add-database", "orders", external.Resource,
                ApplicationName.Parse(name), payload.RootElement, GatewayCommandJsonContext.Default.JsonElement));
            return builder.Build().Model;
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: In-set delivery honors the realized target readiness budget")]
    public async Task StartAsync_InSetTargetWithLongerBudget_ShouldAllowItsCommandToComplete()
    {
        // Arrange
        var events = new List<string>();
        IResourceControlPlane plane = ResourceControlPlane.Create(["database.add-database"]);
        plane.RegisterCommandHandler(new RecordingCommandHandler(events) { ApplyDelay = TimeSpan.FromMilliseconds(150) });
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [new CommandController(events)])
        {
            DirectControlPlane = (_, _) => plane,
            ResourceReadinessBudget = plan => plan.Hints.ContainsKey("cohesion.external")
                ? TimeSpan.FromMilliseconds(25) : TimeSpan.FromSeconds(10),
        };
        IApplicationBuilder provider = CreateBuilder(gateway);
        ResourceManifest manifest = Manifest("db");
        provider.AddResource(manifest);
        IApplicationModel providerModel = provider.Build().Model;
        IApplicationBuilder caller = Application.CreateBuilder((ApplicationName)"caller", ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        caller.AddResource(Manifest("worker", "caller"));
        IApplicationResourceDescriptor external = caller.RemoteReference(
            new ExternalResourceDeclaration("db", "appa", ["admin"], false, manifest),
            options => options.Endpoint("admin", "http://127.0.0.1:12345"));
        using JsonDocument payload = JsonDocument.Parse("{\"name\":\"orders\"}");
        caller.AddCommand(ResourceCommands.Create("database.add-database", "orders", external.Resource, "caller",
            payload.RootElement, GatewayCommandJsonContext.Default.JsonElement));
        IApplicationModel callerModel = caller.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        await ((IMultiModelApplicationGateway)gateway).StartAsync([providerModel, callerModel], cancellation.Token);

        // Assert
        plane.Commands.Single().Owner.ShouldBe("caller");
        gateway.LastExport!.Commands.Single().Status.ShouldBe(ResourceCommandStatus.Applied);
        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: Withdraw a rejected optional declaration without provider deletion")]
    public async Task ReconcileAsync_WithdrawnOptionalRejection_ShouldNotDelete()
    {
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear();
        options.CommandClients.Add(new RecordingCommandClient(events)
        {
            Result = new(ResourceCommandStatus.Rejected, "Unsupported provider operation."),
        });
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder before = CreateBuilder(gateway);
        AddCommandTarget(before, optional: true);
        IApplicationModel initial = before.Build().Model;
        IApplicationBuilder after = CreateBuilder(gateway);
        after.AddResource(Manifest("db"));
        IApplicationModel replacement = after.Build().Model;

        await ((IApplicationGateway)gateway).StartAsync(initial, CancellationToken.None);
        events.Clear();
        await ((IApplicationGateway)gateway).ReconcileAsync(replacement, CancellationToken.None);

        events.ShouldBe(["reconcile:db"]);
        state.GetCommandObservations(initial.Resources[0].Id).ShouldBeEmpty();
        gateway.LastExport!.Commands.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Commands: Changing envelope key withdraws the old declaration before apply")]
    public async Task ReconcileAsync_ChangedEnvelopeKey_ShouldReplaceEvenWithSamePayloadId()
    {
        var events = new List<string>();
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear();
        options.CommandClients.Add(new RecordingCommandClient(events));
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [new CommandController(events)], options: options);
        IApplicationBuilder before = CreateBuilder(gateway);
        AddCommandTarget(before);
        IApplicationModel initial = before.Build().Model;
        IApplicationBuilder after = CreateBuilder(gateway);
        AddCommandTarget(after, key: "renamed");
        IApplicationModel replacement = after.Build().Model;
        initial.Commands.Single().Id.ShouldBe(replacement.Commands.Single().Id);

        await ((IApplicationGateway)gateway).StartAsync(initial, CancellationToken.None);
        events.Clear();
        await ((IApplicationGateway)gateway).ReconcileAsync(replacement, CancellationToken.None);

        events.ShouldBe(["remove:orders", "reconcile:db", "apply:renamed"]);
        gateway.LastExport!.Commands.Single().Key.ShouldBe("renamed");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command inputs: A registered resolver rewrites only the delivered payload through the Sources registrations")]
    public async Task StartAsync_RegisteredCommandInputResolver_ShouldDeliverResolvedPayloadAndKeepDeclaration()
    {
        // Arrange
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var client = new RecordingCommandClient(events);
        var options = new ApplicationGatewayOptions();
        options.Parameters["db-owner"] = "owner-from-parameter";
        options.CommandClients.Clear();
        options.CommandClients.Add(client);
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor target = AddCommandTarget(builder);
        var vault = new RecordingSourceProvider { Secret = Encoding.UTF8.GetBytes("vault-value") };
        builder.Providers.Sources["vault"] = vault;
        var resolver = new RecordingCommandInputResolver("database.add-database", ["parameter:db-owner", "vault:orders-key"]);
        builder.Providers.CommandInputs.Add(resolver);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
            await ((IApplicationGateway)gateway).ReconcileAsync(model, cancellation.Token);

            // Assert
            ResourceCommandInput declared = resolver.Declared.ShouldHaveSingleItem();
            declared.Id.ShouldBe(model.Commands.Single().Id);
            declared.Kind.ShouldBe("database.add-database");
            declared.Owner.ShouldBe("appa");
            declared.Key.ShouldBe("orders");
            Encoding.UTF8.GetString(declared.Payload.Span).ShouldBe("{\"name\":\"orders\"}");
            Encoding.UTF8.GetString(client.Command!.Payload.Span).ShouldBe("owner-from-parameter|vault-value");
            ResourceSourceRequest request = vault.Requests.ShouldHaveSingleItem();
            request.Store.ShouldBeNull();
            request.Key.ShouldBe("orders-key");
            request.Kind.ShouldBe(ResourceMountKind.Secret);
            request.Consumer.ShouldBe((ResourceName)"db");
            // The model and the applied-declaration ledger keep the declaration: the second pass
            // matches it as the identical, already-applied declaration instead of re-delivering.
            Encoding.UTF8.GetString(model.Commands.Single().Payload.Span).ShouldBe("{\"name\":\"orders\"}");
            client.ApplyCount.ShouldBe(1);
            state.GetCommandObservations(target.Resource.Id).Single().Status.ShouldBe(ResourceCommandStatus.Applied);
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command inputs: A resolver refusal rejects the command with its detail and nothing is delivered")]
    public async Task StartAsync_CommandInputResolverRefusal_ShouldRejectWithDetail()
    {
        // Arrange
        var events = new List<string>();
        var state = new InMemoryResourceStateManager();
        var client = new RecordingCommandClient(events);
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear();
        options.CommandClients.Add(client);
        var gateway = new TestGateway(state, [new CommandController(events)], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        IApplicationResourceDescriptor target = AddCommandTarget(builder, optional: true);
        builder.Providers.CommandInputs.Add(new RecordingCommandInputResolver("database.add-database")
        {
            Failure = new InvalidOperationException("database.add-database source 'vault:x' is unresolved."),
        });
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

            // Assert
            ResourceCommandObservation observation = state.GetCommandObservations(target.Resource.Id).Single();
            observation.Status.ShouldBe(ResourceCommandStatus.Rejected);
            observation.Detail.ShouldBe("database.add-database source 'vault:x' is unresolved.");
            client.ApplyCount.ShouldBe(0);
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Command inputs: A resolver for another kind leaves the payload as declared")]
    public async Task StartAsync_ResolverForAnotherKind_ShouldDeliverDeclaredPayload()
    {
        // Arrange
        var events = new List<string>();
        var client = new RecordingCommandClient(events);
        var options = new ApplicationGatewayOptions();
        options.CommandClients.Clear();
        options.CommandClients.Add(client);
        var gateway = new TestGateway(new InMemoryResourceStateManager(), [new CommandController(events)], options: options);
        IApplicationBuilder builder = CreateBuilder(gateway);
        AddCommandTarget(builder);
        var resolver = new RecordingCommandInputResolver("other.rewrite");
        builder.Providers.CommandInputs.Add(resolver);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            // Act
            await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);

            // Assert
            resolver.Declared.ShouldBeEmpty();
            Encoding.UTF8.GetString(client.Command!.Payload.Span).ShouldBe("{\"name\":\"orders\"}");
        }
        finally
        {
            await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
        }
    }

    private static IApplicationResourceDescriptor AddCommandTarget(IApplicationBuilder builder, bool optional = false, string key = "orders")
    {
        IApplicationResourceDescriptor descriptor = builder.AddResource(Manifest("db"));
        using JsonDocument payload = JsonDocument.Parse("{\"name\":\"orders\"}");
        builder.AddCommand(ResourceCommands.Create("database.add-database", key, descriptor.Resource,
            ApplicationName.Parse("appa"), payload.RootElement, GatewayCommandJsonContext.Default.JsonElement, optional));
        return descriptor;
    }

    private static ResourceManifest Manifest(string name, string application = "appa") => new()
    {
        Name = name, Application = application, Kind = "Database",
        ApplicationModel = "Assimalign.Cohesion.Database.ApplicationModel",
        Artifact = new ResourceManifestArtifact { Assembly = "Example.Database.dll" },
        Endpoints = [new ResourceManifestEndpoint { Name = "admin", Scheme = "http", Protocol = "tcp", ContainerPort = 5000 }],
        ControlPlane = new ResourceManifestControlPlane { Endpoint = "admin", Path = "/custom/control" },
        Commands = [new ResourceManifestCommand("database.add-database")],
    };
}
