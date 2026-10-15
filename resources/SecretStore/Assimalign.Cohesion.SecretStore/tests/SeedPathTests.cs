using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.SecretStore.Tests;

public sealed class SeedPathTests
{
    [Theory(DisplayName = "Cohesion Test [SecretStore] - Seed path: invalid input is rejected without throwing")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("trusted-issuers.json")]
    public void TryCreate_WithInvalidPath_ShouldReturnFalse(string? candidate)
    {
        // Arrange and Act
        bool accepted = SecretStoreSeedPath.TryCreate(candidate, out SecretStoreSeedPath path);

        // Assert
        accepted.ShouldBeFalse();
        path.ShouldBe(default);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore] - Seed path: logical identifiers retain exact spelling")]
    [InlineData("apps/api/client-secret")]
    [InlineData("Trusted-issuers.json")]
    [InlineData("certs/api")]
    [InlineData("../relative\\name")]
    [InlineData(" path ")]
    public void TryCreate_WithLogicalPath_ShouldPreserveValue(string candidate)
    {
        // Arrange and Act
        bool accepted = SecretStoreSeedPath.TryCreate(candidate, out SecretStoreSeedPath path);

        // Assert
        accepted.ShouldBeTrue();
        path.Value.ShouldBe(candidate);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore] - Seed path: comparison follows ordinal store identity")]
    public void Equals_WithDifferentCase_ShouldDistinguishPaths()
    {
        // Arrange
        SecretStoreSeedPath first = "app/key";
        SecretStoreSeedPath same = "app/key";
        SecretStoreSeedPath different = "App/key";

        // Act and Assert
        first.ShouldBe(same);
        first.ShouldNotBe(different);
        first.GetHashCode().ShouldBe(same.GetHashCode());
    }
}
