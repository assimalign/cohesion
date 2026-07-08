using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

/// <summary>
/// RFC 6265bis &#167; 5.5 400-day lifetime cap enforced by
/// <see cref="HttpCookie.ClampLifetime(DateTimeOffset, TimeSpan)"/>. Covers the
/// exact-cap, cap-plus-one-second, and deletion (zero/negative) boundaries for
/// <c>Max-Age</c> and the reference-relative clamp for <c>Expires</c>.
/// </summary>
public class HttpCookieLifetimeTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromDays(400);
    private static readonly DateTimeOffset Now = new(2026, 07, 06, 12, 00, 00, TimeSpan.Zero);

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: Max-Age exactly 400 days is left unchanged")]
    public void ClampLifetime_MaxAgeExactlyAtCap_ShouldRemainUnchanged()
    {
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { MaxAge = Cap });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.Options.MaxAge.ShouldBe(Cap);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: Max-Age 400 days + 1s is clamped to 400 days")]
    public void ClampLifetime_MaxAgeOverCapByOneSecond_ShouldClampToCap()
    {
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { MaxAge = Cap + TimeSpan.FromSeconds(1) });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.Options.MaxAge.ShouldBe(Cap);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: negative Max-Age round-trips unchanged (deletion)")]
    public void ClampLifetime_NegativeMaxAge_ShouldRemainUnchanged()
    {
        TimeSpan delete = TimeSpan.FromDays(-1);
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { MaxAge = delete });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.Options.MaxAge.ShouldBe(delete);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: zero Max-Age round-trips unchanged (deletion)")]
    public void ClampLifetime_ZeroMaxAge_ShouldRemainUnchanged()
    {
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { MaxAge = TimeSpan.Zero });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.Options.MaxAge.ShouldBe(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: no lifetime set returns the same instance")]
    public void ClampLifetime_NoLifetime_ShouldReturnSameInstance()
    {
        HttpCookie cookie = new("sid", "v");

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.ShouldBeSameAs(cookie);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: Expires beyond the cap is pulled back to reference + cap")]
    public void ClampLifetime_ExpiresBeyondCap_ShouldClampToReferencePlusCap()
    {
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { Expires = Now + Cap + TimeSpan.FromDays(1) });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.Options.Expires.ShouldBe(Now + Cap);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: Expires exactly at the cap is left unchanged")]
    public void ClampLifetime_ExpiresExactlyAtCap_ShouldRemainUnchanged()
    {
        DateTimeOffset atCap = Now + Cap;
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { Expires = atCap });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.Options.Expires.ShouldBe(atCap);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: a past Expires is left unchanged")]
    public void ClampLifetime_ExpiresInPast_ShouldRemainUnchanged()
    {
        DateTimeOffset past = Now - TimeSpan.FromDays(10);
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { Expires = past });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.Options.Expires.ShouldBe(past);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: default overload caps at 400 days")]
    public void ClampLifetime_DefaultOverload_ShouldCapAt400Days()
    {
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { MaxAge = TimeSpan.FromDays(1000) });

        HttpCookie clamped = cookie.ClampLifetime(Now);

        clamped.Options.MaxAge.ShouldBe(TimeSpan.FromDays(400));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: clamps Max-Age and Expires together")]
    public void ClampLifetime_BothMaxAgeAndExpiresOverCap_ShouldClampBoth()
    {
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions
        {
            MaxAge = Cap + TimeSpan.FromDays(30),
            Expires = Now + Cap + TimeSpan.FromDays(30),
        });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.Options.MaxAge.ShouldBe(Cap);
        clamped.Options.Expires.ShouldBe(Now + Cap);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: does not mutate the original cookie")]
    public void ClampLifetime_WhenClamping_ShouldNotMutateOriginal()
    {
        TimeSpan original = Cap + TimeSpan.FromDays(100);
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { MaxAge = original });

        HttpCookie clamped = cookie.ClampLifetime(Now, Cap);

        clamped.ShouldNotBeSameAs(cookie);
        cookie.Options.MaxAge.ShouldBe(original);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ClampLifetime: negative max lifetime throws")]
    public void ClampLifetime_NegativeMaxLifetime_ShouldThrow()
    {
        HttpCookie cookie = new("sid", "v", new HttpCookieOptions { MaxAge = Cap });

        Should.Throw<ArgumentOutOfRangeException>(() => cookie.ClampLifetime(Now, TimeSpan.FromDays(-1)));
    }
}
