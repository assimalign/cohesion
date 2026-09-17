using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Scheduler.Cron.Tests;

public sealed class CrontabTests
{
    [Theory(DisplayName = "Cohesion Test [Scheduler.Cron] - Parse: requires exactly five fields")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    public void Parse_WithWrongFieldCount_ShouldThrow(string expression)
    {
        Should.Throw<FormatException>(() => Crontab.Parse(expression));
    }

    [Theory(DisplayName = "Cohesion Test [Scheduler.Cron] - Parse: rejects malformed and out-of-range values")]
    [InlineData("*/0 * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("5-2 * * * *")]
    [InlineData("1,,2 * * * *")]
    public void Parse_WithInvalidField_ShouldReject(string expression)
    {
        Crontab.TryParse(expression, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - Parse: supports lists inclusive ranges and steps")]
    public void Parse_WithCombinations_ShouldSelectExpectedValues()
    {
        Crontab expression = Crontab.Parse("1,5-9/2 */6 * * *");

        expression.Minute.Occurrences.ShouldBe([1, 5, 7, 9]);
        expression.Hour.Occurrences.ShouldBe([0, 6, 12, 18]);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - Parse: a maximum integer step cannot overflow")]
    public void Parse_WithMaximumIntegerStep_ShouldSelectOnlyTheStartingValue()
    {
        Crontab expression = Crontab.Parse("2/2147483647 * * * *");

        expression.Minute.Occurrences.ShouldBe([2]);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - Occurrences: parsed values cannot be mutated")]
    public void Occurrences_WhenCastToMutableList_ShouldRemainReadOnly()
    {
        Crontab expression = Crontab.Parse("1,2 * * * *");
        var occurrences = (IList<int>)expression.Minute.Occurrences;

        Should.Throw<NotSupportedException>(() => occurrences[0] = 59);
        expression.Minute.Occurrences.ShouldBe([1, 2]);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - Next: is strictly after the supplied instant")]
    public void GetDateTime_OnMatchingMinute_ShouldAdvance()
    {
        Crontab expression = Crontab.Parse("0 * * * *");

        expression.GetDateTime(new DateTime(2028, 1, 1, 10, 0, 59, DateTimeKind.Utc))
            .ShouldBe(new DateTime(2028, 1, 1, 11, 0, 0, DateTimeKind.Utc));
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - Next: handles leap years and month boundaries")]
    public void GetDateTime_ForLeapDay_ShouldCrossCalendarBoundaries()
    {
        Crontab expression = Crontab.Parse("15 6 29 2 *");

        expression.GetDateTime(new DateTime(2027, 12, 31, 23, 59, 0, DateTimeKind.Utc))
            .ShouldBe(new DateTime(2028, 2, 29, 6, 15, 0, DateTimeKind.Utc));
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - Day fields: use OR when both are restricted")]
    public void GetDateTime_WithRestrictedDayFields_ShouldUseOrSemantics()
    {
        Crontab expression = Crontab.Parse("0 9 13 * 1");

        expression.GetDateTime(new DateTime(2024, 5, 13, 9, 0, 0, DateTimeKind.Utc))
            .ShouldBe(new DateTime(2024, 5, 20, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - Day fields: restricted field governs wildcard")]
    public void GetDateTime_WithWildcardDayOfMonth_ShouldUseWeekday()
    {
        Crontab expression = Crontab.Parse("0 9 * * 1");

        expression.GetDateTime(new DateTime(2024, 5, 14, 0, 0, 0, DateTimeKind.Utc))
            .ShouldBe(new DateTime(2024, 5, 20, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - Sunday: accepts both zero and seven")]
    public void Parse_WithSundayAliases_ShouldNormalize()
    {
        Crontab.Parse("0 0 * * 0").DayOfWeek.Occurrences.ShouldBe([0]);
        Crontab.Parse("0 0 * * 7").DayOfWeek.Occurrences.ShouldBe([0]);
    }
}
