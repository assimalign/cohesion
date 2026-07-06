using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the normalized claim collection, including the canonical
/// multi-value semantics: duplicate claims and array-valued claims flatten identically.
/// </summary>
public sealed class IdentityClaimCollectionTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimCollection: Lookup should be ordinal and insertion-ordered")]
    public void Lookup_WhenQueried_ShouldBeOrdinalAndInsertionOrdered()
    {
        // Arrange
        var collection = new IdentityClaimCollection(
        [
            new IdentityClaim("roles", "reader"),
            new IdentityClaim("email", "user@example.com"),
            new IdentityClaim("roles", "writer"),
        ]);

        // Assert
        collection.Count.ShouldBe(3);
        collection.Contains("roles").ShouldBeTrue();
        collection.Contains("ROLES").ShouldBeFalse();

        collection.TryGet("roles", out var first).ShouldBeTrue();
        first.ShouldNotBeNull();
        first.Value.AsString().ShouldBe("reader");

        var all = collection.GetAll("roles");
        all.Count.ShouldBe(2);
        all[0].Value.AsString().ShouldBe("reader");
        all[1].Value.AsString().ShouldBe("writer");

        collection.GetAll("missing").ShouldBeEmpty();
        collection.TryGet("missing", out var absent).ShouldBeFalse();
        absent.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimCollection: Duplicate claims and array claims should flatten identically")]
    public void GetValues_WhenMultiValueShapesDiffer_ShouldFlattenIdentically()
    {
        // Arrange — the same logical data in its two legal wire-derived shapes:
        // SAML-style duplicate claims vs OIDC-style single array claim.
        var duplicates = new IdentityClaimCollection(
        [
            new IdentityClaim("roles", "reader"),
            new IdentityClaim("roles", "writer"),
        ]);
        var array = new IdentityClaimCollection(
        [
            new IdentityClaim("roles", IdentityClaimValue.FromArray(
                [IdentityClaimValue.FromString("reader"), IdentityClaimValue.FromString("writer")])),
        ]);

        // Act
        var fromDuplicates = duplicates.GetValues("roles").Select(value => value.AsString()).ToArray();
        var fromArray = array.GetValues("roles").Select(value => value.AsString()).ToArray();

        // Assert — a consumer checking roles cannot tell the shapes apart.
        fromDuplicates.ShouldBe(["reader", "writer"]);
        fromArray.ShouldBe(["reader", "writer"]);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimCollection: GetValues should expand exactly one level")]
    public void GetValues_WhenValuesNestArrays_ShouldExpandExactlyOneLevel()
    {
        // Arrange — an array claim whose second element is itself an array. Only the
        // outermost level expands; the nested array surfaces as a single Array-kind value.
        var inner = IdentityClaimValue.FromArray(
            [IdentityClaimValue.FromString("nested-a"), IdentityClaimValue.FromString("nested-b")]);
        var collection = new IdentityClaimCollection(
        [
            new IdentityClaim("data", IdentityClaimValue.FromArray(
                [IdentityClaimValue.FromString("outer"), inner])),
        ]);

        // Act
        var values = collection.GetValues("data");

        // Assert
        values.Count.ShouldBe(2);
        values[0].AsString().ShouldBe("outer");
        values[1].Kind.ShouldBe(IdentityValueKind.Array);
        values[1].ShouldBe(inner);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - ClaimCollection: Constructor should snapshot and guard")]
    public void Constructor_WhenSourceMutatesOrContainsNull_ShouldSnapshotAndGuard()
    {
        // Arrange
        var source = new List<IIdentityClaim> { new IdentityClaim("email", "a@example.com") };
        var collection = new IdentityClaimCollection(source);

        // Act
        source.Add(new IdentityClaim("email", "b@example.com"));

        // Assert
        collection.Count.ShouldBe(1);
        Should.Throw<ArgumentNullException>(() => new IdentityClaimCollection([null!]));
        Should.Throw<ArgumentNullException>(() => new IdentityClaimCollection(null!));
        IdentityClaimCollection.Empty.Count.ShouldBe(0);
    }
}
