using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the open authentication protocol vocabulary.
/// </summary>
public sealed class AuthenticationProtocolTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocol: Default instance should be a functional Unknown")]
    public void DefaultInstance_WhenInspected_ShouldBeFunctionalUnknown()
    {
        var protocol = default(AuthenticationProtocol);

        protocol.ShouldBe(AuthenticationProtocol.Unknown);
        protocol.Name.ShouldBe("unknown");
        protocol.ToString().ShouldBe("unknown");
        (protocol == new AuthenticationProtocol("unknown")).ShouldBeTrue();
        protocol.GetHashCode().ShouldBe(new AuthenticationProtocol("unknown").GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocol: Construction should normalize casing and whitespace")]
    public void Constructor_WhenGivenUnnormalizedNames_ShouldNormalize()
    {
        (new AuthenticationProtocol("SAML2") == AuthenticationProtocol.Saml2).ShouldBeTrue();
        (new AuthenticationProtocol("  OIDC ") == AuthenticationProtocol.OpenIdConnect).ShouldBeTrue();
        (AuthenticationProtocol.OpenIdConnect == AuthenticationProtocol.Saml2).ShouldBeFalse();
        (AuthenticationProtocol.OAuth2 != AuthenticationProtocol.OpenIdConnect).ShouldBeTrue();

        Should.Throw<System.ArgumentException>(() => new AuthenticationProtocol("  "));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocol: Well-known names should be stable")]
    public void WellKnownProtocols_WhenInspected_ShouldHaveStableNames()
    {
        AuthenticationProtocol.OpenIdConnect.Name.ShouldBe("oidc");
        AuthenticationProtocol.OAuth2.Name.ShouldBe("oauth2");
        AuthenticationProtocol.Saml2.Name.ShouldBe("saml2");
        AuthenticationProtocol.Unknown.Name.ShouldBe("unknown");
    }
}
