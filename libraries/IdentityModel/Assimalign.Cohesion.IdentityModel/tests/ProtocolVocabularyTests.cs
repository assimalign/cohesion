using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the open protocol vocabularies: roles, bindings, and endpoint
/// kinds.
/// </summary>
public sealed class ProtocolVocabularyTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Vocabulary defaults should be functional Unknowns")]
    public void Defaults_WhenInspected_ShouldBeFunctionalUnknowns()
    {
        default(ProtocolRole).ShouldBe(ProtocolRole.Unknown);
        default(ProtocolRole).Name.ShouldBe("unknown");
        default(ProtocolBinding).ShouldBe(ProtocolBinding.Unknown);
        default(ProtocolBinding).Name.ShouldBe("unknown");
        default(ProtocolEndpointKind).ShouldBe(ProtocolEndpointKind.Unknown);
        default(ProtocolEndpointKind).Name.ShouldBe("unknown");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Vocabulary construction should normalize")]
    public void Constructors_WhenGivenUnnormalizedNames_ShouldNormalize()
    {
        (new ProtocolRole(" Identity-Provider ") == ProtocolRole.IdentityProvider).ShouldBeTrue();
        (new ProtocolBinding("HTTP-POST") == ProtocolBinding.HttpPost).ShouldBeTrue();
        (new ProtocolEndpointKind(" Token ") == new ProtocolEndpointKind("token")).ShouldBeTrue();

        Should.Throw<ArgumentException>(() => new ProtocolRole(" "));
        Should.Throw<ArgumentException>(() => new ProtocolBinding(""));
        Should.Throw<ArgumentException>(() => new ProtocolEndpointKind(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Well-known role and binding names should be stable")]
    public void WellKnownNames_WhenInspected_ShouldBeStable()
    {
        ProtocolRole.IdentityProvider.Name.ShouldBe("identity-provider");
        ProtocolRole.RelyingParty.Name.ShouldBe("relying-party");
        ProtocolRole.AuthorizationServer.Name.ShouldBe("authorization-server");
        ProtocolRole.ResourceServer.Name.ShouldBe("resource-server");
        ProtocolRole.Issuer.Name.ShouldBe("issuer");
        ProtocolRole.Audience.Name.ShouldBe("audience");

        // One name per wire shape: SAML Redirect and OIDC response_mode=query are the
        // same transport, so there is deliberately no separate "query" value.
        ProtocolBinding.HttpRedirect.Name.ShouldBe("http-redirect");
        ProtocolBinding.HttpPost.Name.ShouldBe("http-post");
        ProtocolBinding.HttpFragment.Name.ShouldBe("http-fragment");
        ProtocolBinding.HttpArtifact.Name.ShouldBe("http-artifact");
        ProtocolBinding.Soap.Name.ShouldBe("soap");
        ProtocolBinding.BackChannel.Name.ShouldBe("back-channel");
    }
}
