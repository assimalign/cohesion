using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

public sealed partial class GatewayControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Caller authenticators: Refusals and command authorization follow the mapped caller")]
    public async Task Authorize_RegisteredCallerAuthenticator_ShouldAuthorizeTheMappedCaller()
    {
        // Arrange
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var dispatcher = new RecordingCommandDispatcher();
        var options = new ApplicationGatewayOptions
        {
            ExportDirectory = root,
            ControlPlane = GatewayControlPlane.CreateFactory(controlPlane =>
            {
                controlPlane.MetadataDirectory = root;
                controlPlane.CommandDispatchers.Add(dispatcher);
            }),
        };
        var gateway = new TestGateway(options);
        var log = new ConcurrentQueue<string>();
        var identityProvider = new RecordingCallerAuthenticator("idp", log, request => request.Credential switch
        {
            "idp-developer" => Admit(new ApplicationCaller(null, "developer@example.test", ApplicationCallerKind.Developer, [])),
            "idp-peer" => Admit(new ApplicationCaller("caller", "caller-gateway", ApplicationCallerKind.Peer, [])),
            "idp-restricted" => Admit(new ApplicationCaller("caller", "caller-gateway", ApplicationCallerKind.Peer, ["test.other"])),
            "idp-anonymous-peer" => Admit(new ApplicationCaller(null, "anonymous", ApplicationCallerKind.Peer, [])),
            "idp-expired" => new ApplicationCallerResult(ApplicationCallerStatus.Unauthorized, null, "The identity provider token expired."),
            "idp-blocked" => new ApplicationCallerResult(ApplicationCallerStatus.Forbidden, null, null),
            _ => default,
        });
        IApplicationBuilder builder = Application
            .CreateBuilder((ApplicationName)"appa", ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        builder.AddResource(CreateManifest("appa", "api", "test", 43110));
        builder.Providers.Callers.Add(identityProvider);
        IApplicationModel model = builder.Build().Model;
        IApplicationGateway control = gateway;
        using var client = new HttpClient();

        try
        {
            await control.StartAsync(model, cancellation.Token);
            (Uri address, _) = ReadMetadata(root, "appa");
            var application = new Uri(address, "/cohesion/v1/application");
            var commands = new Uri(address, "/cohesion/v1/resources/api/commands");

            // Act
            using HttpResponseMessage developerRead = await SendAsync(
                client, HttpMethod.Get, application, "idp-developer", content: null, cancellation.Token);
            using HttpResponseMessage developerWrite = await SendAsync(
                client, HttpMethod.Put, Command(address, "developer-command"), "idp-developer", CommandBody("caller"), cancellation.Token);
            using HttpResponseMessage anonymousWrite = await SendAsync(
                client, HttpMethod.Put, Command(address, "anonymous-command"), "idp-anonymous-peer", CommandBody("caller"), cancellation.Token);
            using HttpResponseMessage peerWrite = await SendAsync(
                client, HttpMethod.Put, Command(address, "peer-command"), "idp-peer", CommandBody("caller"), cancellation.Token);
            using HttpResponseMessage foreignOwner = await SendAsync(
                client, HttpMethod.Put, Command(address, "foreign-command"), "idp-peer", CommandBody("other"), cancellation.Token);
            using HttpResponseMessage restricted = await SendAsync(
                client, HttpMethod.Put, Command(address, "restricted-command"), "idp-restricted", CommandBody("caller", "second"), cancellation.Token);
            using HttpResponseMessage peerRead = await SendAsync(
                client, HttpMethod.Get, commands, "idp-peer", content: null, cancellation.Token);
            using HttpResponseMessage expired = await SendAsync(
                client, HttpMethod.Get, application, "idp-expired", content: null, cancellation.Token);
            using HttpResponseMessage blocked = await SendAsync(
                client, HttpMethod.Get, application, new AuthenticationHeaderValue("DPoP", "idp-blocked"), cancellation.Token);
            using HttpResponseMessage unknown = await SendAsync(
                client, HttpMethod.Get, application, "unknown-credential", content: null, cancellation.Token);

            // Assert: discovery admits any authenticated caller.
            developerRead.StatusCode.ShouldBe(NetHttpStatusCode.OK);

            // Command routes need a peer acting for an application, which owns what it sends.
            developerWrite.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            developerWrite.Headers.WwwAuthenticate.Single().Parameter.ShouldBe("error=\"insufficient_scope\"");
            anonymousWrite.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            peerWrite.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            (await peerWrite.Content.ReadAsStringAsync(cancellation.Token)).ShouldContain("\"status\": \"Applied\"", Case.Sensitive);
            foreignOwner.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            (await ReadJsonStringAsync(foreignOwner, "error", cancellation.Token))
                .ShouldBe("Command owner 'other' does not match authenticated issuer 'caller'.");
            restricted.StatusCode.ShouldBe(NetHttpStatusCode.Conflict);
            (await ReadJsonStringAsync(restricted, "detail", cancellation.Token))
                .ShouldBe("Trusted issuer 'caller' may not send command kind 'test.apply'.");
            peerRead.StatusCode.ShouldBe(NetHttpStatusCode.OK);
            (await peerRead.Content.ReadAsStringAsync(cancellation.Token)).ShouldContain("peer-command", Case.Sensitive);
            dispatcher.Applied.ShouldBe(1);

            // Final refusals from the authenticator, and the unchanged answer when all pass.
            expired.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
            expired.Headers.WwwAuthenticate.Single().ToString().ShouldBe("Bearer error=\"invalid_token\"");
            (await ReadJsonStringAsync(expired, "error", cancellation.Token)).ShouldBe("The identity provider token expired.");

            blocked.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            blocked.Headers.WwwAuthenticate.Single().ToString().ShouldBe("DPoP error=\"insufficient_scope\"");
            identityProvider.Requests.ShouldContain(request => request.Scheme == "DPoP" && request.Credential == "idp-blocked");
            unknown.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
            unknown.Headers.WwwAuthenticate.Single().ToString().ShouldBe("Bearer error=\"invalid_token\"");
            identityProvider.Requests.ShouldAllBe(request => request.Application == (ApplicationName)"appa");
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Identity: A peer admits identity-provider gateway credentials through its caller authenticator")]
    public async Task StartAsync_IdentityProviderPeerCredentials_ShouldApplyAndRemoveRemoteCommands()
    {
        // Arrange: the caller's identity provider issues only its gateway-to-gateway credentials; the
        // provider never trusts the caller's application key and admits it through an authenticator.
        string root = CreateTestDirectory();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        var areaClient = new RecordingGatewayCommandClient();
        var providerOptions = new ApplicationGatewayOptions { ExportDirectory = root };
        providerOptions.CommandClients.Add(areaClient);
        GatewayControlPlane.Configure(providerOptions, GatewayRunMode.Run);
        var provider = new TestGateway(providerOptions);
        var log = new ConcurrentQueue<string>();
        var authenticator = new RecordingCallerAuthenticator("idp", log, request =>
            request.Credential.StartsWith("idp:", StringComparison.Ordinal) &&
            request.Credential.EndsWith(":caller", StringComparison.Ordinal)
                ? Admit(new ApplicationCaller("caller", "caller-gateway", ApplicationCallerKind.Peer, []))
                : default);
        IApplicationBuilder providerBuilder = Application
            .CreateBuilder((ApplicationName)"appa", ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(provider);
        providerBuilder.AddResource(CreateManifest("appa", "api", "test", 43110));
        providerBuilder.Providers.Callers.Add(authenticator);
        IApplicationModel providerModel = providerBuilder.Build().Model;
        var callerOptions = new ApplicationGatewayOptions { ExportDirectory = root };
        GatewayControlPlane.Configure(callerOptions, GatewayRunMode.Describe);
        var caller = new TestGateway(callerOptions);
        var issuer = new IdentityProviderCredentialIssuer(
            ApplicationCredentialPurpose.RemoteCommand,
            ApplicationCredentialPurpose.PeerControlPlane);

        try
        {
            await ((IApplicationGateway)provider).StartAsync(providerModel, cancellation.Token);
            (Uri address, JsonElement providerKey) = ReadMetadata(root, "appa");
            IApplicationBuilder builder = Application
                .CreateBuilder((ApplicationName)"caller", ["--environment", AppEnvironment.Keys.Local])
                .UseGateway(caller);
            builder.AddResource(CreateManifest("caller", "worker", "test", 43111));
            IApplicationResourceDescriptor target = builder.RemoteReference(
                new ExternalResourceDeclaration("api", "appa", ["http"], optional: false, manifest: providerModel.Manifests[0]),
                remote => remote.Gateway(address));
            using JsonDocument payload = JsonDocument.Parse("{\"value\":\"configured\"}");
            builder.AddCommand(ResourceCommands.Create("test.apply", "setting", target.Resource, "caller",
                payload.RootElement, CommandPayloadJsonContext.Default.JsonElement));
            builder.Providers.CredentialIssuer = issuer;
            IApplicationModel callerModel = builder.Build().Model;
            await ((IApplicationTrustGateway)caller).AddTrustedIssuerAsync(callerModel, "appa",
                ApplicationExportDocument.Create(providerModel, "1", trustKey: providerKey), cancellation.Token);

            // Act
            await ((IApplicationGateway)caller).StartAsync(callerModel, cancellation.Token);
            await ((IApplicationGateway)caller).UninstallAsync(callerModel, cancellation.Token);

            // Assert
            areaClient.Applied.ShouldBe(1);
            areaClient.Deleted.ShouldBe(1);
            provider.GetTrustedIssuers(providerModel.Name).ShouldHaveSingleItem().Issuer.ShouldBe("appa");
            authenticator.Requests.ShouldContain(request => request.Credential == "idp:PeerControlPlane:caller");
            authenticator.Requests.ShouldContain(request => request.Credential == "idp:RemoteCommand:caller");
            issuer.Requests.ShouldContain(request =>
                request.Purpose == ApplicationCredentialPurpose.RemoteCommand &&
                request.Audience == "cohesion-export" &&
                request.Subject == "control-plane-test");
            // Every other purpose fell back to the default issuer: the local worker's bootstrap
            // credential is still the caller's application-key token.
            issuer.Requests.ShouldContain(request =>
                request.Purpose == ApplicationCredentialPurpose.ResourceBootstrap && request.Audience == "worker");
        }
        finally
        {
            await ((IApplicationGateway)caller).StopAsync(CancellationToken.None);
            await ((IApplicationGateway)provider).StopAsync(CancellationToken.None);
            DeleteTestDirectory(root);
        }
    }

    private static ApplicationCallerResult Admit(ApplicationCaller caller) =>
        new(ApplicationCallerStatus.Authenticated, caller, null);

    private static async Task<string?> ReadJsonStringAsync(
        HttpResponseMessage response,
        string property,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty(property).GetString();
    }

    private static Uri Command(Uri address, string id) =>
        new(address, $"/cohesion/v1/resources/api/commands/{id}");

    private static StringContent CommandBody(string owner, string key = "setting") =>
        Json($$"""{"kind":"test.apply","owner":"{{owner}}","key":"{{key}}","payload":"aGVsbG8="}""");

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        Uri address,
        AuthenticationHeaderValue authorization,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = authorization;
        return await client.SendAsync(request, cancellationToken);
    }
}
