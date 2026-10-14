using System;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

public sealed class GeneratedValueTypeRedactionTests
{
    private const string displayPrefix = "Cohesion Test [Core] - Generated value-type redaction: ";

    [Fact(DisplayName = displayPrefix + "custom display redacts populated and default values")]
    public void ToString_CustomDisplay_ShouldRedactPopulatedAndDefaultValues()
    {
        // Arrange
        var value = new GeneratedRedactedString("secret");
        GeneratedRedactedString missing = default;

        // Act
        string text = value.ToString();
        string formatted = $"{value}";
        string? boxed = ((object)value).ToString();

        // Assert
        text.ShouldBe("[redacted]");
        formatted.ShouldBe("[redacted]");
        boxed.ShouldBe("[redacted]");
        missing.ToString().ShouldBe("[redacted]");
    }

    [Fact(DisplayName = displayPrefix + "custom display preserves validation and equality")]
    public void TryCreate_CustomDisplay_ShouldPreserveValidationAndEquality()
    {
        // Arrange
        var expected = new GeneratedRedactedString("secret");

        // Act
        bool accepted = GeneratedRedactedString.TryCreate("secret", out var value);
        bool rejected = GeneratedRedactedString.TryCreate(string.Empty, out var invalid);

        // Assert
        accepted.ShouldBeTrue();
        value.Value.ShouldBe("secret");
        value.ShouldBe(expected);
        value.GetHashCode().ShouldBe(expected.GetHashCode());
        rejected.ShouldBeFalse();
        invalid.ShouldBe(default);
        Should.Throw<ArgumentException>(() => new GeneratedRedactedString(string.Empty));
    }

    [Fact(DisplayName = displayPrefix + "custom display preserves explicit JSON values")]
    public void Serialize_CustomDisplay_ShouldPreserveUnderlyingValue()
    {
        // Arrange
        var value = new GeneratedRedactedString("secret");
        var metadata = GeneratedValueTypeJsonContext.Default.GeneratedRedactedString;

        // Act
        string json = JsonSerializer.Serialize(value, metadata);
        GeneratedRedactedString restored = JsonSerializer.Deserialize(json, metadata);

        // Assert
        json.ShouldBe("\"secret\"");
        restored.ShouldBe(value);
        restored.ToString().ShouldBe("[redacted]");
    }
}
