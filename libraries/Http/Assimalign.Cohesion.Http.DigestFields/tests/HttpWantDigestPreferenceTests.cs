using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.DigestFields.Tests;

using Assimalign.Cohesion.Http;

public class HttpWantDigestPreferenceTests
{
    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Preference: Rejects integers outside the wire range at construction")]
    [InlineData(-1_000_000_000_000_000L)]
    [InlineData(1_000_000_000_000_000L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Constructor_OutOfWireRange_ThrowsArgumentOutOfRangeException(long preference)
    {
        // Arrange
        HttpDigestAlgorithm algorithm = HttpDigestAlgorithm.Sha256;

        // Act
        Action construct = () => _ = new HttpWantDigestPreference(algorithm, preference);

        // Assert
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(construct);
        exception.ParamName.ShouldBe("preference");
    }

    [Theory(DisplayName = "Cohesion Test [Http.DigestFields] - Preference: Wire boundaries round-trip through want-digest fields")]
    [InlineData(-999_999_999_999_999L, "sha-256=-999999999999999")]
    [InlineData(0L, "sha-256=0")]
    [InlineData(999_999_999_999_999L, "sha-256=999999999999999")]
    public void Constructor_WireBoundary_RoundTrips(long preference, string expected)
    {
        // Arrange
        var value = new HttpWantDigestPreference(HttpDigestAlgorithm.Sha256, preference);

        // Act
        string serialized = HttpWantDigestField.Create(value).Serialize();
        bool parsed = HttpWantDigestField.TryParse(serialized, out HttpWantDigestField field);

        // Assert
        serialized.ShouldBe(expected);
        parsed.ShouldBeTrue();
        field.Preferences.Count.ShouldBe(1);
        field.Preferences[0].Algorithm.ShouldBe(HttpDigestAlgorithm.Sha256);
        field.Preferences[0].Preference.ShouldBe(preference);
        field.Serialize().ShouldBe(serialized);
    }
}
