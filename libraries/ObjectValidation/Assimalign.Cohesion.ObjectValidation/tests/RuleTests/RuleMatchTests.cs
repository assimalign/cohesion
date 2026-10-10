using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

using Assimalign.Cohesion.ObjectValidation.Internal;

/// <summary>
/// The <c>Matches</c> rule bounds the time a caller's pattern may take, and a match that runs out of time fails the
/// rule instead of passing it (#1377). The pattern is built once, with the caller's options.
/// </summary>
public class RuleMatchTests
{
    // Nested quantifiers: the backtracking engine tries every split of the run of 'a's before the '!' rejects it.
    private const string ExponentialPattern = @"^(a+)+$";

    public sealed class Account
    {
        public string? Code { get; set; }
    }

    public sealed class ExponentialPatternProfile : ValidationProfile<Account>
    {
        public override void Configure(IValidationRuleDescriptor<Account> descriptor)
        {
            descriptor.RuleFor(account => account.Code!).Matches(ExponentialPattern);
        }
    }

    public sealed class InvalidPatternProfile : ValidationProfile<Account>
    {
        public override void Configure(IValidationRuleDescriptor<Account> descriptor)
        {
            descriptor.RuleFor(account => account.Code!).Matches("(");
        }
    }

    private static IValidator CreateValidator<T>(IValidationProfile<T> profile) => Validator.Create(builder => builder.AddProfile(profile));

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Matches: a match that runs out of time fails the rule")]
    public void TryValidate_MatchRunsOutOfTime_ShouldFail()
    {
        // Arrange: 2^40 splits, far beyond the 10 ms timeout.
        var rule = new MatchValidationRule(ExponentialPattern, matchTimeout: TimeSpan.FromMilliseconds(10))
        {
            Error = new ValidationError()
        };

        // Act
        bool invoked = rule.TryValidate((object)(new string('a', 40) + "!"), out var context);

        // Assert
        invoked.ShouldBeTrue();
        context.Errors.Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Matches: a caller's pattern that backtracks fails within the default timeout")]
    public void Validate_PatternBacktracksOnInput_ShouldFailWithinTheTimeout()
    {
        // Arrange: 26 letters take the backtracking engine about 10 s with no timeout.
        IValidator validator = CreateValidator(new ExponentialPatternProfile());
        var account = new Account { Code = new string('a', 26) + "!" };

        // Act
        long started = Stopwatch.GetTimestamp();
        ValidationResult result = validator.Validate(account);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        // Assert
        result.IsValid.ShouldBeFalse();
        elapsed.ShouldBeLessThan(MatchValidationRule.DefaultMatchTimeout + TimeSpan.FromSeconds(4));
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Matches: the caller's options apply to the match")]
    public void TryValidate_IgnoreCaseOption_ShouldMatchRegardlessOfCase()
    {
        // Arrange
        var rule = new MatchValidationRule("^abc$", RegexOptions.IgnoreCase)
        {
            Error = new ValidationError()
        };

        // Act
        bool invoked = rule.TryValidate((object)"ABC", out var context);

        // Assert
        invoked.ShouldBeTrue();
        context.Errors.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Matches: an invalid pattern throws where the profile declares it")]
    public void Matches_InvalidPattern_ShouldThrowWhenDeclared()
    {
        // Act and Assert
        Should.Throw<ArgumentException>(() => CreateValidator(new InvalidPatternProfile()));
    }
}
