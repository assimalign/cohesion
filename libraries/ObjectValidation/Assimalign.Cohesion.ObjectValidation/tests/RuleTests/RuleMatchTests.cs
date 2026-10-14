using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

using Assimalign.Cohesion.ObjectValidation.Internal;

/// <summary>
/// The <c>Matches</c> rule matches a caller's pattern in linear time when the non-backtracking engine supports it,
/// bounds the time any other pattern may take, and fails the rule when a match runs out of time instead of passing
/// it (#1377). The pattern is built once, with the caller's options.
/// </summary>
public class RuleMatchTests
{
    // Nested quantifiers: the backtracking engine tries every split of the run of 'a's before the '!' rejects it.
    private const string ExponentialPattern = @"^(a+)+$";

    // The same nested quantifiers with a backreference, which the non-backtracking engine does not support, so the
    // rule falls back to the backtracking engine and its timeout.
    private const string BacktrackingOnlyPattern = @"^(a+)+\1$";

    public sealed class Account
    {
        public string? Code { get; set; }

        public string[]? Codes { get; set; }
    }

    public sealed class BacktrackingOnlyPatternProfile : ValidationProfile<Account>
    {
        public override void Configure(IValidationRuleDescriptor<Account> descriptor)
        {
            descriptor.RuleFor(account => account.Code!).Matches(BacktrackingOnlyPattern);
        }
    }

    public sealed class ExponentialPatternForEachProfile : ValidationProfile<Account>
    {
        public override void Configure(IValidationRuleDescriptor<Account> descriptor)
        {
            descriptor.RuleForEach(account => account.Codes!).Matches(ExponentialPattern);
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
        var rule = new MatchValidationRule(BacktrackingOnlyPattern, matchTimeout: TimeSpan.FromMilliseconds(10))
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
        // Arrange: 28 letters took the backtracking engine 45 s with no timeout; each two more letters cost about
        // four times as much. Without a timeout the match still fails, so only the time tells.
        IValidator validator = CreateValidator(new BacktrackingOnlyPatternProfile());
        var account = new Account { Code = new string('a', 28) + "!" };

        // Act
        long started = Stopwatch.GetTimestamp();
        ValidationResult result = validator.Validate(account);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        // Assert: a timed-out match overruns its timeout by milliseconds.
        result.IsValid.ShouldBeFalse();
        elapsed.ShouldBeLessThan(MatchValidationRule.DefaultMatchTimeout + TimeSpan.FromSeconds(1.5));
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Matches: a pattern the non-backtracking engine supports costs linear time over every element")]
    public void Validate_SupportedPatternOverEveryElement_ShouldFinishWithinOneMatchTimeout()
    {
        // Arrange: each element costs the backtracking engine about 150 ms, under its timeout, so 1,000 cost minutes.
        IValidator validator = CreateValidator(new ExponentialPatternForEachProfile());
        var account = new Account { Codes = Enumerable.Repeat(new string('a', 20) + "!", 1_000).ToArray() };

        // Act
        long started = Stopwatch.GetTimestamp();
        ValidationResult result = validator.Validate(account);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        // Assert
        result.Errors.Count().ShouldBe(1_000);
        elapsed.ShouldBeLessThan(MatchValidationRule.DefaultMatchTimeout);
    }

    [Theory(DisplayName = "Cohesion Test [ObjectValidation] - Matches: a pattern or option the non-backtracking engine does not support is matched by the backtracking engine")]
    [InlineData(@"^(\w)\1$", RegexOptions.None, "aa", true)]
    [InlineData(@"^(\w)\1$", RegexOptions.None, "ab", false)]
    [InlineData(@"^a(?=b)", RegexOptions.None, "ab", true)]
    [InlineData(@"^abc$", RegexOptions.RightToLeft, "abc", true)]
    [InlineData(@"^abc$", RegexOptions.ECMAScript, "abd", false)]
    public void TryValidate_UnsupportedByNonBacktracking_ShouldMatchWithBacktracking(string pattern, RegexOptions options, string value, bool matches)
    {
        // Arrange
        var rule = new MatchValidationRule(pattern, options)
        {
            Error = new ValidationError()
        };

        // Act
        bool invoked = rule.TryValidate((object)value, out var context);

        // Assert
        invoked.ShouldBeTrue();
        context.Errors.Any().ShouldBe(!matches);
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
