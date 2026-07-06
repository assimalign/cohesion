using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the claim collection convenience accessors.
/// </summary>
public sealed class IdentityClaimCollectionExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimCollection: GetString should return the first string value")]
    public void GetString_WhenQueried_ShouldReturnTheFirstStringValue()
    {
        var collection = new IdentityClaimCollection(
        [
            new IdentityClaim("email", "first@example.com"),
            new IdentityClaim("email", "second@example.com"),
            new IdentityClaim("age", IdentityClaimValue.FromInteger(42)),
        ]);

        collection.GetString("email").ShouldBe("first@example.com");
        collection.GetString("age").ShouldBeNull();      // not a string
        collection.GetString("missing").ShouldBeNull();

        collection.TryGetString("email", out var email).ShouldBeTrue();
        email.ShouldBe("first@example.com");
        collection.TryGetString("age", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimCollection: HasClaim should match across both multi-value shapes")]
    public void HasClaim_WhenShapesDiffer_ShouldMatchIdentically()
    {
        var duplicates = new IdentityClaimCollection(
        [
            new IdentityClaim("roles", "reader"),
            new IdentityClaim("roles", "admin"),
        ]);
        var array = new IdentityClaimCollection(
        [
            new IdentityClaim("roles", IdentityClaimValue.FromArray(
                [IdentityClaimValue.FromString("reader"), IdentityClaimValue.FromString("admin")])),
        ]);

        duplicates.HasClaim("roles", "admin").ShouldBeTrue();
        array.HasClaim("roles", "admin").ShouldBeTrue();
        duplicates.HasClaim("roles", "auditor").ShouldBeFalse();
        array.HasClaim("roles", "auditor").ShouldBeFalse();
        duplicates.HasClaim("roles", "ADMIN").ShouldBeFalse(); // ordinal, case-sensitive
    }
}
