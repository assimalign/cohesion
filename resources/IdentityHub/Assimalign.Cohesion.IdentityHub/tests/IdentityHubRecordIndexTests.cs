using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubRecordIndexTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should keep the name and value and not be unique unless asked")]
    public void Constructor_WithANameAndValue_ShouldExposeThemAndDefaultToNotUnique()
    {
        // Act
        var shared = new IdentityHubRecordIndex("owner", "alice");
        var unique = new IdentityHubRecordIndex("alias", "alice", isUnique: true);

        // Assert
        shared.Name.Value.ShouldBe("owner");
        shared.Value.Value.ShouldBe("alice");
        shared.IsUnique.ShouldBeFalse();
        unique.IsUnique.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a default name or value")]
    public void Constructor_WithADefaultNameOrValue_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException noName = Should.Throw<ArgumentException>(() => new IdentityHubRecordIndex(default, "alice"));
        ArgumentException noValue = Should.Throw<ArgumentException>(() => new IdentityHubRecordIndex("owner", default));

        // Assert
        noName.ParamName.ShouldBe("name");
        noValue.ParamName.ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare the name, the value, and the uniqueness")]
    public void Equals_WithAnotherEntry_ShouldCompareEveryPart()
    {
        // Arrange
        var entry = new IdentityHubRecordIndex("alias", "alice", isUnique: true);

        // Act & Assert
        entry.ShouldBe(new IdentityHubRecordIndex("alias", "alice", isUnique: true));
        entry.ShouldNotBe(new IdentityHubRecordIndex("alias", "alice"));
        entry.ShouldNotBe(new IdentityHubRecordIndex("alias", "Alice", isUnique: true));
        entry.ShouldNotBe(new IdentityHubRecordIndex("owner", "alice", isUnique: true));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should name no index")]
    public void Default_OfAnEntry_ShouldHoldNoNameAndNoValue()
    {
        // Act
        IdentityHubRecordIndex entry = default;

        // Assert
        entry.Name.Value.ShouldBeNull();
        entry.Value.Value.ShouldBeNull();
        entry.IsUnique.ShouldBeFalse();
    }
}
