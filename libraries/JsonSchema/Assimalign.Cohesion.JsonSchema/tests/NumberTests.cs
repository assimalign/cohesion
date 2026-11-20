using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Numbers compare by exact decimal value, never through <see cref="double"/>.
/// </summary>
public class NumberTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Numbers: ";

    [Theory(DisplayName = DisplayPrefix + "multipleOf: Should divide exactly in decimal")]
    [InlineData("0.01", "19.99", true)]
    [InlineData("0.07", "0.21", true)]
    [InlineData("0.1", "0.3", true)]
    [InlineData("0.1", "0.35", false)]
    [InlineData("1e-400", "1", true)]
    [InlineData("3", "1e999999999", false)]
    [InlineData("2", "1e999999999", true)]
    [InlineData("0.5", "-7.5", true)]
    [InlineData("1.5", "4.5000", true)]
    [InlineData("7", "0", true)]
    public void MultipleOf_Decimals_ShouldBeExact(string divisor, string instance, bool expected)
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"multipleOf\": " + divisor + "}");

        // Act & Assert
        document.IsValid(TestJson.Parse(instance)).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "Bounds: Should compare integers beyond double precision exactly")]
    [InlineData("{\"maximum\": 18446744073709551615}", "18446744073709551615", true)]
    [InlineData("{\"maximum\": 18446744073709551615}", "18446744073709551616", false)]
    [InlineData("{\"minimum\": 9007199254740993}", "9007199254740992", false)]
    [InlineData("{\"exclusiveMinimum\": 0.1}", "0.1000000000000000000001", true)]
    [InlineData("{\"exclusiveMaximum\": 1e308}", "1e400", false)]
    [InlineData("{\"maximum\": -1e-400}", "0", false)]
    [InlineData("{\"minimum\": -1e400}", "-1e401", false)]
    public void Bounds_LargeAndPreciseNumbers_ShouldCompareExactly(string schema, string instance, bool expected)
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse(schema);

        // Act & Assert
        document.IsValid(TestJson.Parse(instance)).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "type integer: Should accept any number with a zero fractional part")]
    [InlineData("1.0", true)]
    [InlineData("1e2", true)]
    [InlineData("1e400", true)]
    [InlineData("100000000000000000000000000000000.0", true)]
    [InlineData("-0.0", true)]
    [InlineData("1.5", false)]
    [InlineData("1e-2", false)]
    [InlineData("1.0000000000000000000001", false)]
    public void TypeInteger_Numbers_ShouldUseMathematicalValue(string instance, bool expected)
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"type\": \"integer\"}");

        // Act & Assert
        document.IsValid(TestJson.Parse(instance)).ShouldBe(expected);
    }

    [Theory(DisplayName = DisplayPrefix + "const, enum and uniqueItems: Should treat equal numbers as equal")]
    [InlineData("{\"const\": 1}", "1.0", true)]
    [InlineData("{\"const\": 0.1}", "1e-1", true)]
    [InlineData("{\"const\": 0}", "-0", true)]
    [InlineData("{\"enum\": [10]}", "1e1", true)]
    [InlineData("{\"enum\": [{\"a\": [1.50]}]}", "{\"a\": [1.5]}", true)]
    [InlineData("{\"const\": 9007199254740993}", "9007199254740992", false)]
    [InlineData("{\"uniqueItems\": true}", "[1, 1.0]", false)]
    [InlineData("{\"uniqueItems\": true}", "[{\"a\": 1}, {\"a\": 1e0}]", false)]
    [InlineData("{\"uniqueItems\": true}", "[9007199254740992, 9007199254740993]", true)]
    public void Equality_Numbers_ShouldCompareByValue(string schema, string instance, bool expected)
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse(schema);

        // Act & Assert
        document.IsValid(TestJson.Parse(instance)).ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "Load: Should refuse a multipleOf that is not positive")]
    public void Load_NonPositiveMultipleOf_ShouldThrowInvalidSchema()
    {
        // Act & Assert
        Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse("{\"multipleOf\": 0}")).Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
        Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse("{\"multipleOf\": -2}")).Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }
}
