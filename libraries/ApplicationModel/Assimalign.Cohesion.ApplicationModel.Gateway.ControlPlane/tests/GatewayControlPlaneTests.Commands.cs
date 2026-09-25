using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

public sealed partial class GatewayControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Command dispatch: Forwards application transport trust to HTTPS resources")]
    public async Task Configure_HttpsCommandClient_ShouldReceiveApplicationTrust()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var areaClient = new RecordingGatewayCommandClient { ExpectedScheme = "https" };
        var options = new ApplicationGatewayOptions { ExportDirectory = root };
        options.CommandClients.Add(areaClient);
        GatewayControlPlane.Configure(options, GatewayRunMode.Run);
        var gateway = new TestGateway(options);
        areaClient.ExpectedValidator = gateway.TransportValidator;
        IApplicationBuilder builder = Application.CreateBuilder((ApplicationName)"appa", ["--environment", AppEnvironment.Keys.Local]).UseGateway(gateway);
        builder.AddResource(CreateManifest("appa", "api", "test", 43110, scheme: "https"));
        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;
        IAuthenticatedControlPlaneClient client = GatewayControlPlane.CreateClient();
        var command = new ResourceCommand("trusted-command", "test.apply", "caller", "setting", "value"u8.ToArray());
        try
        {
            await control.StartAsync(model, cancellation.Token);
            (Uri address, _) = ReadMetadata(root, "appa");
            string callerToken = await GrantCommandCallerAsync(gateway, model, "caller", cancellation.Token);

            // Act
            ResourceCommandResult applied = await client.ApplyCommandAsync(address, "api", callerToken, command, cancellation.Token);

            // Assert
            applied.Status.ShouldBe(ResourceCommandStatus.Applied);
            areaClient.Applied.ShouldBe(1);
            areaClient.Validator.ShouldBeSameAs(gateway.TransportValidator);
            gateway.TrustApplication.ShouldBe(model.Name);
            ResourceCommandResult deleted = await client.DeleteCommandAsync(address, "api", callerToken, command, cancellation.Token);
            deleted.Status.ShouldBe(ResourceCommandStatus.Applied);
            areaClient.Deleted.ShouldBe(1);
            areaClient.Validator.ShouldBeSameAs(gateway.TransportValidator);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Claiming gateway: Apply and remove remote declarations through the served peer")]
    public async Task StartAsync_RemoteCommands_ShouldObserveAndRemoveThroughPeer()
    {
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var areaClient = new RecordingGatewayCommandClient();
        var providerOptions = new ApplicationGatewayOptions { ExportDirectory = root };
        providerOptions.CommandClients.Add(areaClient);
        GatewayControlPlane.Configure(providerOptions, GatewayRunMode.Run);
        var provider = new TestGateway(providerOptions);
        IApplicationModel providerModel = BuildModel(provider, "appa", includeUnsupportedResource: false);
        var callerOptions = new ApplicationGatewayOptions { ExportDirectory = root };
        GatewayControlPlane.Configure(callerOptions, GatewayRunMode.Describe);
        var caller = new TestGateway(callerOptions);

        try
        {
            await ((IApplicationGateway)provider).StartAsync(providerModel, cancellation.Token);
            (Uri address, JsonElement providerKey) = ReadMetadata(root, "appa");
            IApplicationBuilder builder = Application.CreateBuilder(
                ApplicationName.Parse("caller"), ["--environment", AppEnvironment.Keys.Local]).UseGateway(caller);
            builder.AddResource(CreateManifest("caller", "worker", "test", 43111));
            IApplicationResourceDescriptor target = builder.RemoteReference(
                new ExternalResourceDeclaration("api", "appa", ["http"], optional: false,
                    manifest: providerModel.Manifests[0]), remote => remote.Gateway(address));
            using JsonDocument payload = JsonDocument.Parse("{\"value\":\"configured\"}");
            builder.AddCommand(ResourceCommands.Create("test.apply", "setting", target.Resource, "caller",
                payload.RootElement, CommandPayloadJsonContext.Default.JsonElement));
            builder.AddCommand(ResourceCommands.Create("test.apply", "reject", target.Resource, "caller",
                "refused", CommandPayloadJsonContext.Default.String, optional: true));
            IApplicationModel callerModel = builder.Build().Model;

            await ((IApplicationTrustGateway)caller).IssueDeveloperTokenAsync(callerModel, "developer", cancellation.Token);
            await ((IApplicationTrustGateway)provider).AddTrustedIssuerAsync(providerModel, "caller",
                ApplicationExportDocument.Create(callerModel, "1", trustKey: caller.GetTrustedIssuers("caller")[0].PublicKey),
                cancellation.Token);
            await ((IApplicationTrustGateway)caller).AddTrustedIssuerAsync(callerModel, "appa",
                ApplicationExportDocument.Create(providerModel, "1", trustKey: providerKey), cancellation.Token);

            await ((IApplicationGateway)caller).StartAsync(callerModel, cancellation.Token);
            await ((IApplicationGateway)caller).ReconcileAsync(callerModel, cancellation.Token);

            areaClient.Applied.ShouldBe(2);
            ApplicationExportDocument export = ApplicationExportDocument.Load(Path.Combine(root, "caller", "export.json"));
            export.Commands.Single(command => command.Key == "setting").Status.ShouldBe(ResourceCommandStatus.Applied);
            export.Commands.Single(command => command.Key == "reject").Detail
                .ShouldContain("The provider refused the requested setting.", Case.Sensitive);
            await ((IApplicationGateway)caller).StopAsync(cancellation.Token);
            // Teardown may run in a fresh gateway process: it must load its persisted signing key.
            var teardown = new TestGateway(callerOptions);
            await ((IApplicationGateway)teardown).UninstallAsync(callerModel, cancellation.Token);
            areaClient.Deleted.ShouldBe(1);
        }
        finally
        {
            await ((IApplicationGateway)caller).StopAsync(CancellationToken.None);
            await ((IApplicationGateway)provider).StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Command client: Should preserve outcomes, ownership, and confirmed deletion through configured area clients")]
    public async Task Configure_CommandClients_ShouldDispatchAuthenticatedCommandsAndPreserveOwnership()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var areaClient = new RecordingGatewayCommandClient();
        var options = new ApplicationGatewayOptions { ExportDirectory = root };
        options.CommandClients.Add(areaClient);
        GatewayControlPlane.Configure(options, GatewayRunMode.Run);
        var gateway = new TestGateway(options);
        IApplicationModel model = BuildModel(gateway, "appa", includeUnsupportedResource: false);
        IApplicationGateway control = gateway;
        IAuthenticatedControlPlaneClient client = GatewayControlPlane.CreateClient();
        var command = new ResourceCommand("owned-command", "test.apply", "caller", "setting", "value"u8.ToArray());

        try
        {
            await control.StartAsync(model, cancellation.Token);
            (Uri address, _) = ReadMetadata(root, "appa");
            string callerToken = await GrantCommandCallerAsync(gateway, model, "caller", cancellation.Token);
            string otherToken = await GrantCommandCallerAsync(gateway, model, "other", cancellation.Token);

            // Act: the Configure adapter forwards the target endpoint and bootstrap credential.
            ResourceCommandResult applied = await client.ApplyCommandAsync(
                address, "api", callerToken, command, cancellation.Token);
            ResourceCommandResult replayed = await client.ApplyCommandAsync(
                address, "api", callerToken, command, cancellation.Token);
            ResourceCommandResult rejected = await client.ApplyCommandAsync(
                address, "api", callerToken,
                command with { Id = "provider-rejection", Key = "reject" }, cancellation.Token);

            // Assert
            applied.Status.ShouldBe(ResourceCommandStatus.Applied);
            applied.Detail.ShouldContain("Applied by the dispatcher", Case.Sensitive);
            Encoding.UTF8.GetString(applied.Result.Span).ShouldBe("accepted");
            replayed.Status.ShouldBe(ResourceCommandStatus.Applied);
            replayed.Detail.ShouldBe(applied.Detail);
            rejected.Status.ShouldBe(ResourceCommandStatus.Rejected);
            rejected.Detail.ShouldContain("The provider refused the requested setting.", Case.Sensitive);
            areaClient.Applied.ShouldBe(2);

            // A refused apply never reserved ownership and needs no provider deletion.
            ResourceCommandResult rejectedRemoved = await client.DeleteCommandAsync(
                address, "api", callerToken,
                command with { Id = "provider-rejection", Key = "reject" }, cancellation.Token);
            rejectedRemoved.Status.ShouldBe(ResourceCommandStatus.Applied);
            areaClient.Deleted.ShouldBe(0);

            // Act: another trusted caller cannot replace the existing owner's key or delete it.
            ResourceCommandResult conflict = await client.ApplyCommandAsync(
                address, "api", otherToken,
                command with { Id = "other-command", Owner = "other" }, cancellation.Token);
            ResourceCommandResult foreignDelete = await client.DeleteCommandAsync(
                address, "api", otherToken, command, cancellation.Token);

            // Assert
            conflict.Status.ShouldBe(ResourceCommandStatus.Rejected);
            conflict.Detail.ShouldBe("Command key 'setting' for kind 'test.apply' is owned by application 'caller'.");
            foreignDelete.Status.ShouldBe(ResourceCommandStatus.Rejected);
            foreignDelete.Detail.ShouldContain("does not match authenticated issuer 'other'", Case.Sensitive);
            areaClient.Applied.ShouldBe(2);
            areaClient.Deleted.ShouldBe(0);

            // Act: a rejected deletion retains the declaration until the provider confirms deletion.
            areaClient.RejectDeletion = true;
            ResourceCommandResult retained = await client.DeleteCommandAsync(
                address, "api", callerToken, command, cancellation.Token);
            using var http = new HttpClient();
            using HttpResponseMessage retainedList = await SendAsync(
                http, HttpMethod.Get, new Uri(address, "/cohesion/v1/resources/api/commands"),
                callerToken, content: null, cancellation.Token);

            // Assert
            retained.Status.ShouldBe(ResourceCommandStatus.Rejected);
            retained.Detail.ShouldContain("The provider could not remove the setting.", Case.Sensitive);
            (await retainedList.Content.ReadAsStringAsync(cancellation.Token))
                .ShouldContain("owned-command", Case.Sensitive);
            (await retainedList.Content.ReadAsStringAsync(cancellation.Token))
                .ShouldNotContain("provider-rejection", Case.Sensitive);

            ResourceCommandResult retainedOwnership = await client.ApplyCommandAsync(
                address, "api", otherToken,
                command with { Id = "after-rejected-delete", Owner = "other" }, cancellation.Token);
            retainedOwnership.Status.ShouldBe(ResourceCommandStatus.Rejected);
            retainedOwnership.Detail.ShouldBe("Command key 'setting' for kind 'test.apply' is owned by application 'caller'.");
            areaClient.Applied.ShouldBe(2);

            // Act
            areaClient.RejectDeletion = false;
            ResourceCommandResult deleted = await client.DeleteCommandAsync(
                address, "api", callerToken, command, cancellation.Token);
            using HttpResponseMessage remaining = await SendAsync(
                http, HttpMethod.Get, new Uri(address, "/cohesion/v1/resources/api/commands"),
                callerToken, content: null, cancellation.Token);

            // Assert
            deleted.Status.ShouldBe(ResourceCommandStatus.Applied);
            deleted.Detail.ShouldBe("Owned command removed by the peer gateway.");
            areaClient.Deleted.ShouldBe(2);
            (await remaining.Content.ReadAsStringAsync(cancellation.Token))
                .ShouldNotContain("owned-command", Case.Sensitive);

            // The confirmed delete releases the reservation for a subsequent owner.
            areaClient.ExpectedOwner = "other";
            ResourceCommandResult reassigned = await client.ApplyCommandAsync(
                address, "api", otherToken,
                command with { Id = "after-confirmed-delete", Owner = "other" }, cancellation.Token);
            reassigned.Status.ShouldBe(ResourceCommandStatus.Applied);
            areaClient.Applied.ShouldBe(3);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Trust allow kinds restrict apply and delete while empty grants remain unrestricted")]
    public async Task Commands_AllowedKinds_ShouldEnforceBothMutations()
    {
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var areaClient = new RecordingGatewayCommandClient();
        var options = new ApplicationGatewayOptions { ExportDirectory = root };
        options.CommandClients.Add(areaClient);
        GatewayControlPlane.Configure(options, GatewayRunMode.Run);
        var gateway = new TestGateway(options);
        IApplicationModel model = BuildModel(gateway, "appa", includeUnsupportedResource: false);
        IApplicationGateway control = gateway;
        IAuthenticatedControlPlaneClient client = GatewayControlPlane.CreateClient();
        (string token, TrustedIssuer issuer) = CreateControlPlaneToken(
            "caller", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), allowCommands: true);
        IApplicationModel peer = BuildModel(new TestGateway(new ApplicationGatewayOptions()), "caller", includeUnsupportedResource: false);
        ApplicationExportDocument export = ApplicationExportDocument.Create(peer, "1", trustKey: issuer.PublicKey);
        var command = new ResourceCommand("allowed", "test.apply", "caller", "setting", "value"u8.ToArray());
        try
        {
            await control.StartAsync(model, cancellation.Token);
            (Uri address, _) = ReadMetadata(root, "appa");
            await ((IApplicationTrustGateway)gateway).AddTrustedIssuerAsync(model, "caller", export, cancellation.Token);
            (await client.ApplyCommandAsync(address, "api", token, command, cancellation.Token)).Status.ShouldBe(ResourceCommandStatus.Applied);
            await ((IApplicationTrustGateway)gateway).AddTrustedIssuerAsync(model, "caller", export, ["test.other"], cancellation.Token);
            ResourceCommandResult apply = await client.ApplyCommandAsync(address, "api", token,
                command with { Id = "restricted", Key = "second" }, cancellation.Token);
            ResourceCommandResult delete = await client.DeleteCommandAsync(address, "api", token, command, cancellation.Token);
            foreach (ResourceCommandResult refused in new[] { apply, delete })
            {
                refused.Status.ShouldBe(ResourceCommandStatus.Rejected);
                refused.Detail.ShouldBe("Trusted issuer 'caller' may not send command kind 'test.apply'.");
            }
            areaClient.Applied.ShouldBe(1);
            areaClient.Deleted.ShouldBe(0);
            await ((IApplicationTrustGateway)gateway).AddTrustedIssuerAsync(model, "caller", export, [], cancellation.Token);
            (await client.DeleteCommandAsync(address, "api", token, command, cancellation.Token)).Status.ShouldBe(ResourceCommandStatus.Applied);
            areaClient.Deleted.ShouldBe(1);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    private static async Task<string> GrantCommandCallerAsync(
        TestGateway gateway,
        IApplicationModel model,
        string caller,
        CancellationToken cancellationToken = default)
    {
        (string token, TrustedIssuer issuer) = CreateControlPlaneToken(
            caller, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), allowCommands: true);
        var peerGateway = new TestGateway(new ApplicationGatewayOptions());
        IApplicationModel peerModel = BuildModel(peerGateway, caller, includeUnsupportedResource: false);
        await ((IApplicationTrustGateway)gateway).AddTrustedIssuerAsync(
            model, caller, ApplicationExportDocument.Create(peerModel, "1", trustKey: issuer.PublicKey), cancellationToken);
        return token;
    }
}
