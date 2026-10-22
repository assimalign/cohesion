using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ObjectValidation.Tests.TestObjects;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

public class StringRuleFastPathTests
{
    [Theory(DisplayName = "Cohesion Test [ObjectValidation] - String checks preserve null, whitespace and UTF-16 length behavior")]
    [InlineData(null, 5)]
    [InlineData("", 4)]
    [InlineData(" ", 4)]
    [InlineData("  ", 2)]
    [InlineData("ab", 2)]
    [InlineData("abc", 1)]
    [InlineData("abcd", 4)]
    [InlineData("😀", 2)]
    [InlineData("a\u0301", 2)]
    [InlineData("\u2003", 4)]
    public void Validate_StringRules_PreservesExistingFailures(string? value, int expected)
    {
        // Arrange
        var validator = CreateValidator();

        // Act
        var result = validator.Validate(new StringRuleModel { Value = value });

        // Assert
        result.Errors.Count().ShouldBe(expected);
        result.Invocations.Count().ShouldBe(6);
        result.Invocations.All(invocation => invocation.Invoked).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Long string rules preserve failures and invocation records")]
    public void Validate_LongString_PreservesExistingFailures()
    {
        // Arrange
        var validator = CreateValidator();
        var value = new string('a', 4096);

        // Act
        var result = validator.Validate(new StringRuleModel { Value = value });

        // Assert
        result.Errors.Count().ShouldBe(4);
        result.Invocations.Count().ShouldBe(6);
    }

    private static IValidator CreateValidator() => Validator.Create(builder =>
    {
        builder.AddOptions(options => options.ContinueThroughValidationChain = true);
        builder.AddProfile(profiles => profiles.CreateProfile<StringRuleModel>(rules => rules
            .RuleFor(model => model.Value!).Empty().NotEmpty().Length(3).MinLength(2).MaxLength(3).Length(2, 3)));
    });
}
