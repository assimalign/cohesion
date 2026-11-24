using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubRecordKeyTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a record key")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("Alice@Example.com")]
    [InlineData("tenant-1/users/42")]
    [InlineData("urn:cohesion:client:web")]
    [InlineData(" leading and trailing spaces ")]
    [InlineData("ключ")]
    [InlineData("鍵")]
    [InlineData("\U0001F511")]
    [InlineData("3q2-7w==")]
    public void IsValid_WithARecordKey_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityHubRecordKey.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityHubRecordKey(value).Value.ShouldBe(value);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse a control character, an unpaired surrogate, and an empty key")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason()
    {
        // Arrange: built at run time, because an unpaired surrogate cannot be an attribute argument.
        string[] values =
        [
            string.Empty,
            "line\nbreak",
            "tab\tseparated",
            "nul\0",
            "delete\u007F",
            "c1\u0085control",
            new string([(char)0xD83D]),
            new string([(char)0xDD11]),
            new string(['a', (char)0xD83D, 'b']),
            new string([(char)0xDD11, (char)0xD83D]),
            new string(['a', (char)0xD83D]),
        ];

        foreach (string value in values)
        {
            // Act
            bool valid = IdentityHubRecordKey.IsValid(value, out string? message);

            // Assert
            valid.ShouldBeFalse($"a key of {value.Length} characters starting U+{(value.Length is 0 ? 0 : value[0]):X4}");
            message.ShouldNotBeNullOrWhiteSpace();
            Should.Throw<ArgumentException>(() => new IdentityHubRecordKey(value));
            IdentityHubRecordKey.TryCreate(value, out _).ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should not echo the refused key in its reason")]
    public void IsValid_WithARefusedKey_ShouldNotEchoTheValue()
    {
        // Act
        IdentityHubRecordKey.IsValid("secret-looking\nvalue", out string? message);

        // Assert
        message.ShouldNotBeNull();
        message.ShouldNotContain("secret-looking");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest key and refuse one character more")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('k', IdentityHubRecordKey.MaxLength);
        string tooLong = new('k', IdentityHubRecordKey.MaxLength + 1);

        // Act & Assert
        IdentityHubRecordKey.MaxLength.ShouldBe(512);
        IdentityHubRecordKey.IsValid(longest, out _).ShouldBeTrue();
        IdentityHubRecordKey.IsValid(tooLong, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare keys ordinally and never fold case")]
    public void Equals_WithKeysThatDifferByCase_ShouldTreatThemAsDistinct()
    {
        // Arrange
        IdentityHubRecordKey lower = "alice";
        IdentityHubRecordKey upper = "Alice";

        // Act & Assert
        (lower == upper).ShouldBeFalse();
        (lower == new IdentityHubRecordKey("alice")).ShouldBeTrue();
        lower.GetHashCode().ShouldBe(new IdentityHubRecordKey("alice").GetHashCode());
        (upper < lower).ShouldBeTrue();
        lower.CompareTo(upper).ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no key")]
    public void Default_OfAKey_ShouldHoldNoValue()
    {
        // Act
        IdentityHubRecordKey key = default;

        // Assert
        key.Value.ShouldBeNull();
        key.ToString().ShouldBe(string.Empty);
        IdentityHubRecordKey.TryCreate(null, out _).ShouldBeFalse();
    }
}
