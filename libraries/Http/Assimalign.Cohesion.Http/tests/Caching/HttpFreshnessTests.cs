using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9111 &#167; 4.2 compliance tests for <see cref="HttpFreshness"/>: freshness-lifetime selection
/// (&#167; 4.2.1), the current-age algorithm (&#167; 4.2.3), and the freshness comparison.
/// </summary>
public class HttpFreshnessTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ============================================================================
    // Freshness lifetime (§4.2.1)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: shared cache prefers s-maxage")]
    public void GetFreshnessLifetime_SharedWithSMaxAge_ShouldUseSMaxAge()
    {
        HttpCacheControl cc = HttpCacheControl.Parse("max-age=60, s-maxage=120");

        HttpFreshness.GetFreshnessLifetime(cc, expires: null, date: null, shared: true)
            .ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: private cache ignores s-maxage")]
    public void GetFreshnessLifetime_PrivateWithSMaxAge_ShouldUseMaxAge()
    {
        HttpCacheControl cc = HttpCacheControl.Parse("max-age=60, s-maxage=120");

        HttpFreshness.GetFreshnessLifetime(cc, expires: null, date: null, shared: false)
            .ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: falls back to Expires minus Date")]
    public void GetFreshnessLifetime_ExpiresAndDate_ShouldSubtract()
    {
        HttpCacheControl cc = default;

        HttpFreshness.GetFreshnessLifetime(cc, expires: Base.AddSeconds(300), date: Base, shared: false)
            .ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: past Expires clamps to zero")]
    public void GetFreshnessLifetime_ExpiresBeforeDate_ShouldClampToZero()
    {
        HttpCacheControl cc = default;

        HttpFreshness.GetFreshnessLifetime(cc, expires: Base, date: Base.AddSeconds(60), shared: false)
            .ShouldBe(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: no explicit lifetime returns null")]
    public void GetFreshnessLifetime_NoDirectives_ShouldReturnNull()
    {
        HttpCacheControl cc = default;

        HttpFreshness.GetFreshnessLifetime(cc, expires: null, date: null, shared: false).ShouldBeNull();
        HttpFreshness.GetFreshnessLifetime(cc, expires: Base, date: null, shared: false).ShouldBeNull();
    }

    // ============================================================================
    // Current age (§4.2.3)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: current age follows §4.2.3 algorithm")]
    public void CalculateCurrentAge_ShouldFollowAlgorithm()
    {
        DateTimeOffset date = Base;
        DateTimeOffset responseTime = Base.AddSeconds(3);   // apparent_age = 3
        DateTimeOffset requestTime = responseTime.AddSeconds(-2); // response_delay = 2
        DateTimeOffset now = responseTime.AddSeconds(4);    // resident_time = 4
        TimeSpan ageHeader = TimeSpan.FromSeconds(10);      // corrected_age_value = 12

        // corrected_initial_age = max(3, 12) = 12; current_age = 12 + 4 = 16
        TimeSpan age = HttpFreshness.CalculateCurrentAge(ageHeader, date, requestTime, responseTime, now);

        age.ShouldBe(TimeSpan.FromSeconds(16));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: apparent age dominates when Age is small")]
    public void CalculateCurrentAge_LargeApparentAge_ShouldDominate()
    {
        DateTimeOffset date = Base;
        DateTimeOffset responseTime = Base.AddSeconds(100); // apparent_age = 100
        DateTimeOffset requestTime = responseTime;          // response_delay = 0
        DateTimeOffset now = responseTime;                  // resident_time = 0

        TimeSpan age = HttpFreshness.CalculateCurrentAge(TimeSpan.FromSeconds(5), date, requestTime, responseTime, now);

        age.ShouldBe(TimeSpan.FromSeconds(100));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: null Age and Date treated as zero")]
    public void CalculateCurrentAge_NoAgeNoDate_ShouldUseResidentTime()
    {
        DateTimeOffset responseTime = Base;
        DateTimeOffset now = Base.AddSeconds(7);

        TimeSpan age = HttpFreshness.CalculateCurrentAge(ageValue: null, dateValue: null, requestTime: Base, responseTime: responseTime, now: now);

        age.ShouldBe(TimeSpan.FromSeconds(7));
    }

    // ============================================================================
    // IsFresh
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFreshness: fresh while lifetime exceeds age")]
    public void IsFresh_ShouldCompareLifetimeAndAge()
    {
        HttpFreshness.IsFresh(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)).ShouldBeTrue();
        HttpFreshness.IsFresh(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)).ShouldBeFalse();
        HttpFreshness.IsFresh(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90)).ShouldBeFalse();
    }
}
