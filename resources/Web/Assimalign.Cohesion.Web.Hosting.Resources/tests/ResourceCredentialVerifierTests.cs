using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

/// <summary>
/// Verifies that the control-plane middleware consults a registered resource credential verifier first,
/// falls through to the default application-key verification on NoResult, and authorizes the mapped caller.
/// </summary>
public sealed class ResourceCredentialVerifierTests
{
    private const string displayPrefix = "Cohesion Test [Web.Hosting.Resources] - Credential verifier: ";
    private const string application = "tests";
    private const string resource = "resource";
    private const string gateway = "gateway";

    [Fact(DisplayName = displayPrefix + "NoResult falls through to the application-key verification")]
    public async Task InvokeAsync_WhenRegisteredVerifierReturnsNoResult_FallsThroughToApplicationKey()
    {
        // Arrange
        using var identity = new TestBootstrapIdentity(application, gateway);
        ResourceContext context = CreateContext(identity);
        var verifier = new ScriptedCredentialVerifier(default);
        verifier.BindTo(context, "WebCredentialFallThrough");
        string token = identity.Issue(resource);

        // Act
        HttpStatusCode accepted = await InvokeAsync(context, "Bearer " + token);
        HttpStatusCode missing = await InvokeAsync(context, authorization: null);
        HttpStatusCode foreign = await InvokeAsync(context, "Bearer " + identity.Issue("other-resource"));

        // Assert
        accepted.ShouldBe(HttpStatusCode.Ok);
        missing.ShouldBe(HttpStatusCode.Unauthorized);
        foreign.ShouldBe(HttpStatusCode.Forbidden);
        verifier.Presentations.Count.ShouldBe(3);
        verifier.Presentations[0].Scheme.ShouldBe("Bearer");
        verifier.Presentations[0].Credential.ShouldBe(token);
        verifier.Presentations[0].ExpectedAudience.ShouldBe(resource);
        verifier.Presentations[1].Scheme.ShouldBeEmpty();
    }

    [Fact(DisplayName = displayPrefix + "An authorized gateway caller of this application is admitted without an application key")]
    public async Task InvokeAsync_WhenRegisteredVerifierAuthorizesGateway_Serves()
    {
        // Arrange
        using var identity = new TestBootstrapIdentity(application, gateway);
        ResourceContext context = CreateContext(identity);
        var verifier = new ScriptedCredentialVerifier(new ResourceCredentialVerification(
            ResourceCredentialStatus.Authorized,
            new ResourceCaller(application, gateway, ResourceCallerKind.Gateway, []),
            null));
        verifier.BindTo(context, "WebCredentialAuthorized");

        // Act
        HttpStatusCode status = await InvokeAsync(context, "DPoP opaque-credential");

        // Assert
        status.ShouldBe(HttpStatusCode.Ok);
        verifier.Presentations.ShouldHaveSingleItem().Scheme.ShouldBe("DPoP");
        verifier.Presentations[0].Credential.ShouldBe("opaque-credential");
    }

    [Theory(DisplayName = displayPrefix + "An authorized caller that is not this application's gateway is forbidden")]
    [InlineData("other", gateway, ResourceCallerKind.Gateway)]
    [InlineData(application, "other-gateway", ResourceCallerKind.Gateway)]
    [InlineData(application, gateway, ResourceCallerKind.Peer)]
    [InlineData(application, gateway, ResourceCallerKind.Developer)]
    [InlineData(application, gateway, ResourceCallerKind.TelemetryEmitter)]
    public async Task InvokeAsync_WhenMappedCallerIsNotThisGateway_Forbids(
        string callerApplication,
        string callerSubject,
        ResourceCallerKind kind)
    {
        // Arrange
        using var identity = new TestBootstrapIdentity(application, gateway);
        ResourceContext context = CreateContext(identity);
        var verifier = new ScriptedCredentialVerifier(new ResourceCredentialVerification(
            ResourceCredentialStatus.Authorized,
            new ResourceCaller(callerApplication, callerSubject, kind, []),
            null));
        verifier.BindTo(context, $"WebCredentialForbidden-{callerApplication}-{callerSubject}-{kind}");

        // Act
        HttpStatusCode status = await InvokeAsync(context, "Bearer " + identity.Issue(resource));

        // Assert
        status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = displayPrefix + "A registered refusal is final even for a valid application-key token")]
    public async Task InvokeAsync_WhenRegisteredVerifierRefuses_DoesNotFallThrough()
    {
        // Arrange
        using var identity = new TestBootstrapIdentity(application, gateway);
        ResourceContext context = CreateContext(identity);
        var verifier = new ScriptedCredentialVerifier(new ResourceCredentialVerification(
            ResourceCredentialStatus.Unauthorized,
            null,
            "revoked"));
        verifier.BindTo(context, "WebCredentialRefused");

        // Act
        (HttpStatusCode status, string? challenge) = await InvokeWithChallengeAsync(context, "Bearer " + identity.Issue(resource));

        // Assert
        status.ShouldBe(HttpStatusCode.Unauthorized);
        challenge.ShouldBe("Bearer");
    }

    private static async Task<HttpStatusCode> InvokeAsync(ResourceContext context, string? authorization) =>
        (await InvokeWithChallengeAsync(context, authorization)).Status;

    private static async Task<(HttpStatusCode Status, string? Challenge)> InvokeWithChallengeAsync(
        ResourceContext context,
        string? authorization)
    {
        var plane = new RecordingControlPlane();
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get);
        if (authorization is not null)
        {
            exchange.Request.Headers[HttpHeaderKey.Authorization] = authorization;
        }

        await ResourceControlPlaneMiddleware.InvokeAsync(plane, context, true, null, exchange,
            _ => throw new InvalidOperationException("A control-plane route must be terminal."));
        string? challenge = exchange.Response.Headers.TryGetValue(HttpHeaderKey.WWWAuthenticate, out HttpHeaderValue value)
            ? value.Value
            : null;
        return (exchange.Response.StatusCode, challenge);
    }

    private static ResourceContext CreateContext(TestBootstrapIdentity identity) =>
        new(application, resource, "Testing", gateway, contentRootPath: null, endpoints: null, mounts: null,
            settings: null, references: null, bootstrapCredential: ReadOnlyMemory<byte>.Empty,
            applicationTrustKey: identity.PublicKey, ambientValues: null);
}
