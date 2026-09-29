using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ProviderRecordTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider records: ToString redacts credentials and private keys")]
    public void ToString_SecretMembers_ShouldBeRedacted()
    {
        // Arrange
        var connection = new ResourceProviderConnection(
            "appa",
            "secrets",
            "KeyStore",
            new Uri("https://127.0.0.1:7443/cohesion/v1"),
            "bearer-secret-value",
            ServerCertificateValidator: null);
        var credential = new ApplicationCredential("Bearer", "credential-secret-value", DateTimeOffset.UnixEpoch);
        var request = new ApplicationCallerRequest("appa", "Bearer", "caller-secret-value");
        var certificate = new ResourceCertificate("private-key-secret-value", "root-anchor");
        var sourceRequest = new ResourceSourceRequest("appa", "api", "db", ResourceMountKind.Secret, "db", connection);

        // Act
        string[] rendered =
        [
            connection.ToString(),
            credential.ToString(),
            request.ToString(),
            certificate.ToString(),
            sourceRequest.ToString(),
        ];

        // Assert
        foreach (string text in rendered)
        {
            text.ShouldNotContain("secret-value", Case.Sensitive);
            text.ShouldContain("<redacted>", Case.Sensitive);
        }

        rendered[0].ShouldContain("Resource = secrets", Case.Sensitive);
        rendered[3].ShouldContain("TrustAnchorsPem = root-anchor", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider records: required members are guarded")]
    public void Constructors_InvalidRequiredMembers_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() =>
            new ResourceProviderBinding<ITrustedIssuerStore>("secrets", null!));
        Should.Throw<ArgumentNullException>(() => new ResourceCertificate(null!, "root"));
        Should.Throw<ArgumentNullException>(() => new ResourceCertificate("leaf", null!));
        Should.Throw<ArgumentException>(() => new ApplicationCredential(" ", "value", DateTimeOffset.UnixEpoch));
        Should.Throw<ArgumentException>(() => new ApplicationCredential("Bearer", "", DateTimeOffset.UnixEpoch));
        Should.Throw<ArgumentException>(() =>
            new ApplicationCaller(null, " ", ApplicationCallerKind.Developer, []));
        Should.Throw<ArgumentNullException>(() =>
            new ApplicationCaller(null, "dev", ApplicationCallerKind.Developer, null!));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider records: ApplicationCaller snapshots its command kinds")]
    public void ApplicationCaller_AllowedCommandKinds_ShouldBeCopied()
    {
        // Arrange
        var kinds = new List<string> { "test.create" };

        // Act
        var caller = new ApplicationCaller("peer", "peer-gateway", ApplicationCallerKind.Peer, kinds);
        kinds.Add("test.drop");

        // Assert
        caller.AllowedCommandKinds.ShouldBe(["test.create"]);
        caller.Application.ShouldBe((ApplicationName)"peer");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider records: an authenticated caller result must carry its caller")]
    public void ApplicationCallerResult_AuthenticatedWithoutCaller_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            new ApplicationCallerResult(ApplicationCallerStatus.Authenticated, null, null));
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Provider records: non-authenticated caller results need no caller")]
    [InlineData(ApplicationCallerStatus.NoResult)]
    [InlineData(ApplicationCallerStatus.Unauthorized)]
    [InlineData(ApplicationCallerStatus.Forbidden)]
    public void ApplicationCallerResult_NotAuthenticated_ShouldAllowNullCaller(ApplicationCallerStatus status)
    {
        // Act
        var result = new ApplicationCallerResult(status, null, "denied");

        // Assert
        result.Status.ShouldBe(status);
        result.Caller.ShouldBeNull();
        default(ApplicationCallerResult).Status.ShouldBe(ApplicationCallerStatus.NoResult);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider records: an authenticated caller result keeps its caller")]
    public void ApplicationCallerResult_AuthenticatedWithCaller_ShouldKeepCaller()
    {
        // Arrange
        var caller = new ApplicationCaller(null, "developer", ApplicationCallerKind.Developer, []);

        // Act
        var result = new ApplicationCallerResult(ApplicationCallerStatus.Authenticated, caller, null);

        // Assert
        result.Caller.ShouldBeSameAs(caller);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Provider records: a binding may name no resource")]
    public void ResourceProviderBinding_WithoutResource_ShouldKeepProvider()
    {
        // Arrange
        var store = new FakeTrustedIssuerStore();

        // Act
        var binding = new ResourceProviderBinding<ITrustedIssuerStore>(null, store);

        // Assert
        binding.Resource.ShouldBeNull();
        binding.Provider.ShouldBeSameAs(store);
    }
}
