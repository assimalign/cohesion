using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Tests;

/// <summary>
/// Contains unit tests for the protocol endpoint model.
/// </summary>
public sealed class ProtocolEndpointTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Endpoint should preserve wire-exact locations")]
    public void Constructor_WhenConstructed_ShouldPreserveWireExactLocations()
    {
        // The default port must NOT be normalized away — endpoint comparison is a
        // signed-value security control in SAML and exact-match for OAuth redirects.
        var endpoint = new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Kind = new ProtocolEndpointKind("assertion-consumer"),
            Location = "https://sp.example.com:443/acs",
            ResponseLocation = "https://sp.example.com:443/acs/response",
            Binding = ProtocolBinding.HttpPost,
            Role = ProtocolRole.RelyingParty,
            Index = 1,
            IsDefault = true,
        });

        endpoint.Location.ShouldBe("https://sp.example.com:443/acs");
        endpoint.ResponseLocation.ShouldBe("https://sp.example.com:443/acs/response");
        endpoint.Binding.ShouldBe(ProtocolBinding.HttpPost);
        endpoint.Role.ShouldBe(ProtocolRole.RelyingParty);
        endpoint.Index.ShouldBe(1);
        endpoint.IsDefault.ShouldBe(true);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Endpoint IsDefault should preserve the unstated state")]
    public void IsDefault_WhenUnstated_ShouldBeNull()
    {
        // SAML default-endpoint selection distinguishes absent from explicit false: the
        // default is the first endpoint marked true, else the first NOT explicitly marked
        // false, else the first endpoint in document order.
        var unstated = new ProtocolEndpoint(new ProtocolEndpointDescriptor { Location = "https://sp.example/acs" });
        var explicitlyNot = new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Location = "https://sp.example/acs2",
            IsDefault = false,
        });

        unstated.IsDefault.ShouldBeNull();
        explicitlyNot.IsDefault.ShouldBe(false);
        unstated.Role.ShouldBeNull(); // entity-wide when no role scope was declared
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Endpoint materialization should reject invalid locations")]
    public void Constructor_WhenLocationIsInvalid_ShouldThrow()
    {
        Should.Throw<IdentityModelException>(() => new ProtocolEndpoint(new ProtocolEndpointDescriptor()));
        Should.Throw<IdentityModelException>(() => new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Location = "not an absolute uri",
        }));
        Should.Throw<IdentityModelException>(() => new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Location = "https://sp.example/acs",
            ResponseLocation = "/relative/path",
        }));
        Should.Throw<ArgumentNullException>(() => new ProtocolEndpoint(null!));

        // System.Uri would accept all of these as "absolute" (platform-dependently for
        // some); the explicit-scheme rule rejects them identically on every OS.
        Should.Throw<IdentityModelException>(() => new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Location = "//evil.example/cb", // scheme-relative
        }));
        Should.Throw<IdentityModelException>(() => new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Location = @"C:\temp\metadata.xml", // implicit file path
        }));
        Should.Throw<IdentityModelException>(() => new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Location = " https://idp.example/sso", // whitespace-padded
        }));

        // Private-use schemes (native app redirects) are legal absolute URIs.
        Should.NotThrow(() => new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Location = "com.example.app:/oauth2redirect",
        }));
    }
}
