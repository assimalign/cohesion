using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Tests;

/// <summary>
/// Contains unit tests for the protocol party reference.
/// </summary>
public sealed class ProtocolPartyTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Party equality should span identifier and role only")]
    public void Equals_WhenCompared_ShouldSpanIdentifierAndRoleOnly()
    {
        var reference = new ProtocolParty("https://idp.example", ProtocolRole.IdentityProvider, "Example IdP");

        reference.ShouldBe(new ProtocolParty("https://idp.example", ProtocolRole.IdentityProvider));
        reference.GetHashCode().ShouldBe(new ProtocolParty("https://idp.example", ProtocolRole.IdentityProvider).GetHashCode());

        // Same entity in a different role is a different party entry.
        reference.ShouldNotBe(new ProtocolParty("https://idp.example", ProtocolRole.RelyingParty));
        reference.ShouldNotBe(new ProtocolParty("https://other.example", ProtocolRole.IdentityProvider));

        // Display name and properties are descriptive detail.
        var annotated = new ProtocolParty("https://idp.example", ProtocolRole.IdentityProvider, "Another Name",
            new Dictionary<string, IdentityClaimValue> { ["tier"] = "gold" });
        reference.ShouldBe(annotated);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Party construction should guard and snapshot")]
    public void Constructor_WhenGivenInvalidOrMutatedInput_ShouldGuardAndSnapshot()
    {
        Should.Throw<ArgumentException>(() => new ProtocolParty(" ", ProtocolRole.IdentityProvider));

        var properties = new Dictionary<string, IdentityClaimValue> { ["a"] = IdentityClaimValue.FromInteger(1) };
        var party = new ProtocolParty("client-1", ProtocolRole.RelyingParty, properties: properties);

        properties["b"] = IdentityClaimValue.FromInteger(2);

        party.Properties.Count.ShouldBe(1);
        (party == null).ShouldBeFalse();
        (party != new ProtocolParty("client-1", ProtocolRole.RelyingParty)).ShouldBeFalse();
    }
}
