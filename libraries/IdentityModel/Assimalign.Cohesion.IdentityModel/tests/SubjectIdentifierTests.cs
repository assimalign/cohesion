using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the protocol-neutral subject identifier.
/// </summary>
public sealed class SubjectIdentifierTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SubjectIdentifier: Equality should include all four scope fields")]
    public void Equals_WhenScopeFieldsDiffer_ShouldNotBeEqual()
    {
        var reference = new SubjectIdentifier("x9f2", SubjectIdentifierFormats.Persistent, "https://idp.example", "https://sp-a");

        reference.ShouldBe(new SubjectIdentifier("x9f2", SubjectIdentifierFormats.Persistent, "https://idp.example", "https://sp-a"));
        reference.ShouldNotBe(new SubjectIdentifier("other", SubjectIdentifierFormats.Persistent, "https://idp.example", "https://sp-a"));
        reference.ShouldNotBe(new SubjectIdentifier("x9f2", SubjectIdentifierFormats.Transient, "https://idp.example", "https://sp-a"));
        reference.ShouldNotBe(new SubjectIdentifier("x9f2", SubjectIdentifierFormats.Persistent, "https://other-idp.example", "https://sp-a"));

        // SAML Core §8.3: the same persistent value scoped to two SPs is two DISTINCT identities.
        reference.ShouldNotBe(new SubjectIdentifier("x9f2", SubjectIdentifierFormats.Persistent, "https://idp.example", "https://sp-b"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SubjectIdentifier: Properties should not participate in equality")]
    public void Equals_WhenOnlyPropertiesDiffer_ShouldBeEqual()
    {
        // SPProvidedID is spec-defined as non-matching, so it rides in Properties.
        var left = new SubjectIdentifier("user-1", issuer: "https://idp.example",
            properties: new Dictionary<string, string> { ["SPProvidedID"] = "legacy-1" });
        var right = new SubjectIdentifier("user-1", issuer: "https://idp.example",
            properties: new Dictionary<string, string> { ["SPProvidedID"] = "legacy-2" });

        left.ShouldBe(right);
        left.GetHashCode().ShouldBe(right.GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SubjectIdentifier: Absent format should normalize to Unspecified")]
    public void Constructor_WhenFormatIsAbsent_ShouldNormalizeToUnspecified()
    {
        var withNull = new SubjectIdentifier("user-1");
        var withExplicit = new SubjectIdentifier("user-1", SubjectIdentifierFormats.Unspecified);

        withNull.Format.ShouldBe(SubjectIdentifierFormats.Unspecified);
        withNull.ShouldBe(withExplicit);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SubjectIdentifier: Constructor should guard and snapshot")]
    public void Constructor_WhenGivenInvalidOrMutatedInput_ShouldGuardAndSnapshot()
    {
        Should.Throw<ArgumentException>(() => new SubjectIdentifier(""));
        Should.Throw<ArgumentException>(() => new SubjectIdentifier("   "));

        var properties = new Dictionary<string, string> { ["a"] = "1" };
        var identifier = new SubjectIdentifier("user-1", properties: properties);

        properties["b"] = "2";

        identifier.Properties.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SubjectIdentifier: Operators should handle null")]
    public void Operators_WhenComparedWithNull_ShouldBehave()
    {
        var identifier = new SubjectIdentifier("user-1");

        (identifier == null).ShouldBeFalse();
        (null == identifier).ShouldBeFalse();
        (identifier != null).ShouldBeTrue();
        ((SubjectIdentifier?)null == (SubjectIdentifier?)null).ShouldBeTrue();
    }
}
