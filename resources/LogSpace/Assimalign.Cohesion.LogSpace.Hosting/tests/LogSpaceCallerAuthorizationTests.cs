using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.LogSpace.Hosting.Internal;

namespace Assimalign.Cohesion.LogSpace.Hosting.Tests;

/// <summary>
/// Verifies the sink's mapped-caller rules, which apply to a registered verifier's verdict and to the
/// default application-key verdict alike: ingestion admits only this application's telemetry emitters and
/// management admits only this application's gateway.
/// </summary>
public sealed class LogSpaceCallerAuthorizationTests
{
    private const string displayPrefix = "Cohesion Test [LogSpace.Hosting] - Caller authorization: ";
    private const string application = "app";
    private const string gateway = "local";

    [Theory(DisplayName = displayPrefix + "Each verified caller is admitted only by the route it may use")]
    [InlineData(true, application, "web", ResourceCallerKind.TelemetryEmitter, ResourceCredentialStatus.Authorized)]
    [InlineData(true, application, " ", ResourceCallerKind.TelemetryEmitter, ResourceCredentialStatus.Forbidden)]
    [InlineData(true, "other", "web", ResourceCallerKind.TelemetryEmitter, ResourceCredentialStatus.Forbidden)]
    [InlineData(true, application, gateway, ResourceCallerKind.Gateway, ResourceCredentialStatus.Forbidden)]
    [InlineData(false, application, gateway, ResourceCallerKind.Gateway, ResourceCredentialStatus.Authorized)]
    [InlineData(false, application, "other-gateway", ResourceCallerKind.Gateway, ResourceCredentialStatus.Forbidden)]
    [InlineData(false, "other", gateway, ResourceCallerKind.Gateway, ResourceCredentialStatus.Forbidden)]
    [InlineData(false, application, "web", ResourceCallerKind.TelemetryEmitter, ResourceCredentialStatus.Forbidden)]
    [InlineData(false, application, gateway, ResourceCallerKind.Peer, ResourceCredentialStatus.Forbidden)]
    public void AuthorizeCaller_WhenCallerIsAuthorized_AppliesRouteRules(
        bool telemetry,
        string callerApplication,
        string callerSubject,
        ResourceCallerKind kind,
        ResourceCredentialStatus expected)
    {
        // Arrange
        var verification = new ResourceCredentialVerification(
            ResourceCredentialStatus.Authorized,
            new ResourceCaller(callerApplication, callerSubject, kind, []),
            null);

        // Act
        ResourceCredentialStatus status = LogSpaceHttp.AuthorizeCaller(verification, CreateContext(), telemetry, out string? emitter);

        // Assert
        status.ShouldBe(expected);
        emitter.ShouldBe(status is ResourceCredentialStatus.Authorized && telemetry ? callerSubject : null);
    }

    [Theory(DisplayName = displayPrefix + "An unverified or refused credential is never admitted")]
    [InlineData(ResourceCredentialStatus.NoResult, ResourceCredentialStatus.Unauthorized)]
    [InlineData(ResourceCredentialStatus.Unauthorized, ResourceCredentialStatus.Unauthorized)]
    [InlineData(ResourceCredentialStatus.Forbidden, ResourceCredentialStatus.Forbidden)]
    public void AuthorizeCaller_WhenCredentialIsNotAuthorized_Refuses(
        ResourceCredentialStatus verified,
        ResourceCredentialStatus expected)
    {
        // Arrange
        var verification = new ResourceCredentialVerification(
            verified,
            new ResourceCaller(application, gateway, ResourceCallerKind.Gateway, []),
            null);

        // Act
        ResourceCredentialStatus management = LogSpaceHttp.AuthorizeCaller(verification, CreateContext(), false, out string? emitter);

        // Assert
        management.ShouldBe(expected);
        emitter.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "An authorized verdict without a caller is forbidden")]
    public void AuthorizeCaller_WhenAuthorizedWithoutCaller_Forbids()
    {
        // Arrange
        var verification = new ResourceCredentialVerification(ResourceCredentialStatus.Authorized, null, null);

        // Act
        ResourceCredentialStatus status = LogSpaceHttp.AuthorizeCaller(verification, CreateContext(), true, out string? emitter);

        // Assert
        status.ShouldBe(ResourceCredentialStatus.Forbidden);
        emitter.ShouldBeNull();
    }

    private static ResourceContext CreateContext() =>
        new(application, "logs", "Development", gateway, contentRootPath: null, endpoints: null, mounts: null,
            settings: null, references: null, bootstrapCredential: ReadOnlyMemory<byte>.Empty,
            applicationTrustKey: ReadOnlyMemory<byte>.Empty, ambientValues: null);
}
