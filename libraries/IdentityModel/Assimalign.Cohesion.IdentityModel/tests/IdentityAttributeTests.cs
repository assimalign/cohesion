using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the multi-value attribute contract.
/// </summary>
public sealed class IdentityAttributeTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Attribute: Zero-value attributes should be legal and distinct from null values")]
    public void Constructor_WhenValuesAreEmpty_ShouldBeLegal()
    {
        // SAML permits declaring an attribute without disclosing values; that is not the
        // same as an attribute whose value is explicitly null.
        var empty = new IdentityAttribute("memberOf", Array.Empty<IdentityClaimValue>());
        var nullValued = new IdentityAttribute("memberOf", [IdentityClaimValue.Null]);

        empty.Values.ShouldBeEmpty();
        nullValued.Values.Count.ShouldBe(1);
        nullValued.Values[0].IsNull.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Attribute: Constructor should preserve SAML attribute fidelity")]
    public void Constructor_WhenConstructed_ShouldPreserveAttributeFidelity()
    {
        // Arrange — a typical eduPerson attribute where NameFormat, FriendlyName, and
        // xsi:type all occur together.
        var provenance = new IdentityClaimProvenance(
            AuthenticationProtocol.Saml2,
            originalValueType: "xs:string",
            originalNameFormat: "urn:oasis:names:tc:SAML:2.0:attrname-format:uri");

        // Act
        var attribute = new IdentityAttribute(
            "urn:oid:2.16.840.1.113730.3.1.241",
            [IdentityClaimValue.FromString("Ada Lovelace")],
            nameFormat: "urn:oasis:names:tc:SAML:2.0:attrname-format:uri",
            friendlyName: "displayName",
            provenance: provenance);

        // Assert
        attribute.Name.ShouldBe("urn:oid:2.16.840.1.113730.3.1.241");
        attribute.NameFormat.ShouldBe("urn:oasis:names:tc:SAML:2.0:attrname-format:uri");
        attribute.FriendlyName.ShouldBe("displayName");
        attribute.Provenance!.OriginalValueType.ShouldBe("xs:string");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Attribute: Constructor should snapshot and guard")]
    public void Constructor_WhenSourceMutatesOrIsInvalid_ShouldSnapshotAndGuard()
    {
        // Arrange
        var values = new List<IdentityClaimValue> { IdentityClaimValue.FromString("a") };
        var attribute = new IdentityAttribute("groups", values);

        // Act
        values.Add(IdentityClaimValue.FromString("b"));

        // Assert
        attribute.Values.Count.ShouldBe(1);
        Should.Throw<ArgumentException>(() => new IdentityAttribute("", values));
        Should.Throw<ArgumentException>(() => new IdentityAttribute("groups", [default]));
        Should.Throw<ArgumentNullException>(() => new IdentityAttribute("groups", null!));
    }
}
