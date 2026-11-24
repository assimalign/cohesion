using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubEntryTests
{
    private static readonly TestGrant _grant = new("code-1", "ABCD-EFGH", "client-1", Subject: null, ExpiresAt: DateTimeOffset.UnixEpoch);

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should keep the key, value, and version")]
    public void Constructor_WithAKeyValueAndVersion_ShouldExposeThem()
    {
        // Act
        var entry = new IdentityHubEntry<TestGrant>("code-1", _grant, "42");

        // Assert
        entry.Key.Value.ShouldBe("code-1");
        entry.Value.ShouldBeSameAs(_grant);
        entry.Version.Value.ShouldBe("42");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a default key or version and a null value")]
    public void Constructor_WithADefaultKeyOrVersionOrNullValue_ShouldThrow()
    {
        // Act
        ArgumentException noKey = Should.Throw<ArgumentException>(() => new IdentityHubEntry<TestGrant>(default, _grant, "42"));
        ArgumentNullException noValue = Should.Throw<ArgumentNullException>(() => new IdentityHubEntry<TestGrant>("code-1", null!, "42"));
        ArgumentException noVersion = Should.Throw<ArgumentException>(() => new IdentityHubEntry<TestGrant>("code-1", _grant, default));

        // Assert
        noKey.ParamName.ShouldBe("key");
        noValue.ParamName.ShouldBe("value");
        noVersion.ParamName.ShouldBe("version");
    }
}
