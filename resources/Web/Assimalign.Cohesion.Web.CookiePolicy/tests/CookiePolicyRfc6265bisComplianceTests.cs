using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests;

/// <summary>
/// Compliance with the rules RFC 6265bis (draft-ietf-httpbis-rfc6265bis-22) places on the cookies a
/// server sends and that a user agent enforces by ignoring a cookie: the <c>__Secure-</c> and
/// <c>__Host-</c> name prefixes (&#167; 4.1.3, matched case-insensitively per &#167; 5.4),
/// <c>SameSite=None</c> requiring <c>Secure</c> (&#167; 5.7 step 19), the 400-day lifetime limit
/// (&#167; 5.5, 34,560,000 seconds), <c>Max-Age</c> precedence over <c>Expires</c> (&#167; 5.7), and one
/// <c>Set-Cookie</c> field line per cookie (RFC 6265 &#167; 3).
/// </summary>
public class CookiePolicyRfc6265bisComplianceTests
{
    // ---- __Secure- (RFC 6265bis 4.1.3.1) -------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 4.1.3.1: A __Secure- cookie without Secure should be upgraded to Secure")]
    public async Task UseCookiePolicy_SecurePrefixWithoutSecureUnderUpgrade_ShouldAddSecure()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("__Secure-id", "1")));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Name.ShouldBe("__Secure-id");
        field.Has("Secure").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 4.1.3.1: A __Secure- cookie without Secure should be dropped and reported under Reject")]
    public async Task UseCookiePolicy_SecurePrefixWithoutSecureUnderReject_ShouldDropAndReport()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.PrefixViolation = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("__Secure-id", "1"));
            context.Response.Cookies.Add(new HttpCookie("__Secure-ok", "1", new HttpCookieOptions { Secure = true }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Name.ShouldBe("__Secure-ok");
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("__Secure-id", CookiePolicyRejectionReason.SecurePrefixViolation));
    }

    // ---- __Host- (RFC 6265bis 4.1.3.2) ---------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 4.1.3.2: A __Host- cookie should be upgraded to Secure, Path=/ and no Domain")]
    public async Task UseCookiePolicy_HostPrefixViolationsUnderUpgrade_ShouldRepairTheCookie()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("__Host-id", "1", new HttpCookieOptions
            {
                Domain = "example.com",
                Path = "/admin",
            })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Raw.ShouldBe("__Host-id=1; Path=/; Secure");
        field.Has("Domain").ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 4.1.3.2: Each __Host- violation should drop the cookie under Reject")]
    [InlineData(false, "/", null)]
    [InlineData(true, "/admin", null)]
    [InlineData(true, null, null)]
    [InlineData(true, "/", "example.com")]
    public async Task UseCookiePolicy_HostPrefixViolationUnderReject_ShouldDropAndReport(bool secure, string? path, string? domain)
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.Secure = CookieSecurePolicy.None;
            options.PrefixViolation = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("__Host-id", "1", new HttpCookieOptions
            {
                Secure = secure,
                Path = path,
                Domain = domain,
            })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldBeEmpty();
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("__Host-id", CookiePolicyRejectionReason.HostPrefixViolation));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 4.1.3.2: A compliant __Host- cookie should pass unchanged under Reject")]
    public async Task UseCookiePolicy_CompliantHostPrefixUnderReject_ShouldIssueUnchanged()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.PrefixViolation = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("__Host-id", "1", new HttpCookieOptions { Secure = true, Path = "/" })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Raw.ShouldBe("__Host-id=1; Path=/; Secure");
        recorder.Rejections.ShouldBeEmpty();
    }

    // ---- Case-insensitive prefix matching (RFC 6265bis 5.4) -------------------------------------

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.4: Prefixes should be matched case-insensitively, as a user agent matches them")]
    [InlineData("__SECURE-id", CookiePolicyRejectionReason.SecurePrefixViolation)]
    [InlineData("__secure-id", CookiePolicyRejectionReason.SecurePrefixViolation)]
    [InlineData("__HOST-id", CookiePolicyRejectionReason.HostPrefixViolation)]
    [InlineData("__host-id", CookiePolicyRejectionReason.HostPrefixViolation)]
    public async Task UseCookiePolicy_MiscapitalizedPrefix_ShouldBeHeldToThePrefixRules(string name, CookiePolicyRejectionReason expected)
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.PrefixViolation = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie(name, "1")));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldBeEmpty();
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe((name, expected));
    }

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 4.1.3: A prefix anywhere but the start of the name should not apply")]
    [InlineData("x__Host-id")]
    [InlineData("_Host-id")]
    [InlineData("__Hostid")]
    [InlineData("Secure-id")]
    [InlineData("__Securex")]
    public async Task UseCookiePolicy_NameWithoutALeadingPrefix_ShouldNotBeHeldToPrefixRules(string name)
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.PrefixViolation = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie(name, "1", new HttpCookieOptions { Domain = "example.com", Path = "/x" })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Raw.ShouldBe($"{name}=1; Domain=example.com; Path=/x");
        recorder.Rejections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.7: A nameless cookie cannot be constructed, so a prefix cannot hide in the value")]
    public void HttpCookie_EmptyName_ShouldBeRejectedByTheModel()
    {
        // RFC 6265bis 5.7 step 22 has user agents ignore a nameless cookie whose value starts with a prefix.
        // The model never emits a nameless cookie, so the policy has nothing to enforce there.
        Should.Throw<ArgumentException>(() => new HttpCookie(string.Empty, "__Host-id=1"));
    }

    // ---- SameSite=None requires Secure (RFC 6265bis 5.7 step 19) --------------------------------

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.7: SameSite=None without Secure should be upgraded to Secure")]
    public async Task UseCookiePolicy_SameSiteNoneWithoutSecureUnderUpgrade_ShouldAddSecure()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("embed", "1", new HttpCookieOptions { SameSite = HttpCookieSameSiteMode.None })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Raw.ShouldBe("embed=1; Path=/; Secure; SameSite=None");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.7: SameSite=None without Secure should be dropped and reported under Reject")]
    public async Task UseCookiePolicy_SameSiteNoneWithoutSecureUnderReject_ShouldDropAndReport()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.SameSiteNoneWithoutSecure = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("embed", "1", new HttpCookieOptions { SameSite = HttpCookieSameSiteMode.None }));
            context.Response.Cookies.Add(new HttpCookie("secure-embed", "1", new HttpCookieOptions
            {
                SameSite = HttpCookieSameSiteMode.None,
                Secure = true,
            }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Name.ShouldBe("secure-embed");
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("embed", CookiePolicyRejectionReason.SameSiteNoneWithoutSecure));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.7: A Secure floor should satisfy the SameSite=None pairing before Reject is considered")]
    public async Task UseCookiePolicy_SameSiteNoneUnderSecureAlwaysAndReject_ShouldIssueSecure()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.Secure = CookieSecurePolicy.Always;
            options.SameSiteNoneWithoutSecure = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("embed", "1", new HttpCookieOptions { SameSite = HttpCookieSameSiteMode.None })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Raw.ShouldBe("embed=1; Path=/; Secure; SameSite=None");
        recorder.Rejections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.7: A minimum SameSite raised above None should take the cookie out of the pairing rule")]
    public async Task UseCookiePolicy_SameSiteNoneRaisedByMinimum_ShouldNotRequireSecure()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.MinimumSameSitePolicy = HttpCookieSameSiteMode.Lax;
            options.SameSiteNoneWithoutSecure = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("embed", "1", new HttpCookieOptions { SameSite = HttpCookieSameSiteMode.None })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Raw.ShouldBe("embed=1; Path=/; SameSite=Lax");
        recorder.Rejections.ShouldBeEmpty();
    }

    // ---- The 400-day lifetime limit (RFC 6265bis 5.5), applied at emission --------------------------

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.5: A Max-Age over 400 days should be capped at 34560000 seconds")]
    public async Task UseCookiePolicy_MaxAgeOver400Days_ShouldBeCappedAtEmission()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactoryAtFixedTime();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("long", "1", new HttpCookieOptions { MaxAge = TimeSpan.FromDays(500) })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Get("Max-Age").ShouldBe("34560000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.5: An Expires beyond 400 days should be pulled back to the clock plus 400 days")]
    public async Task UseCookiePolicy_ExpiresBeyond400Days_ShouldBeCappedAgainstTheClock()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactoryAtFixedTime();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("long", "1", new HttpCookieOptions
            {
                Expires = CookiePolicyTestHost.Now.AddYears(5),
            })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Get("Expires").ShouldBe(CookiePolicyTestHost.Now.AddDays(400).ToString("R", CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.5: A lifetime within the cap should be emitted unchanged")]
    public async Task UseCookiePolicy_LifetimeWithinTheCap_ShouldBeUnchanged()
    {
        // Arrange
        DateTimeOffset expires = CookiePolicyTestHost.Now.AddDays(399);
        await using WebApplicationTestFactory factory = CreateFactoryAtFixedTime();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("short", "1", new HttpCookieOptions
            {
                Expires = expires,
                MaxAge = TimeSpan.FromDays(30),
            })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Get("Expires").ShouldBe(expires.ToString("R", CultureInfo.InvariantCulture));
        field.Get("Max-Age").ShouldBe("2592000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Lifetime: A configured cap shorter than 400 days should apply to both Max-Age and Expires")]
    public async Task UseCookiePolicy_ShorterConfiguredCap_ShouldCapBothLifetimeAttributes()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.TimeProvider = new FixedTimeProvider(CookiePolicyTestHost.Now);
            options.MaxLifetime = TimeSpan.FromDays(30);
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1", new HttpCookieOptions
            {
                Expires = CookiePolicyTestHost.Now.AddDays(90),
                MaxAge = TimeSpan.FromDays(90),
            })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Get("Max-Age").ShouldBe("2592000");
        field.Get("Expires").ShouldBe(CookiePolicyTestHost.Now.AddDays(30).ToString("R", CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.6.2: A deletion (Max-Age=0, past Expires) should pass the cap untouched")]
    public async Task UseCookiePolicy_DeletionCookie_ShouldPassTheCapUntouched()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactoryAtFixedTime();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("gone", string.Empty, new HttpCookieOptions
            {
                Expires = DateTimeOffset.UnixEpoch,
                MaxAge = TimeSpan.Zero,
            })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Raw.ShouldBe("gone=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Max-Age=0");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265bis 5.7: Max-Age should win over a past Expires when the policy decides what a deletion is")]
    public async Task UseCookiePolicy_PositiveMaxAgeWithPastExpires_ShouldNotBeTreatedAsADeletion()
    {
        // Arrange — consent is required and absent, so only a deletion may pass. Max-Age=3600 makes this
        // cookie live for an hour on a conforming user agent, whatever its Expires says.
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.CheckConsentNeeded = _ => true;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("live", "1", new HttpCookieOptions
            {
                Expires = DateTimeOffset.UnixEpoch,
                MaxAge = TimeSpan.FromHours(1),
            }));
            context.Response.Cookies.Add(new HttpCookie("gone", string.Empty, new HttpCookieOptions
            {
                Expires = DateTimeOffset.UnixEpoch,
            }));
            context.Response.Cookies.Add(new HttpCookie("gone-too", string.Empty, new HttpCookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(1),
                MaxAge = TimeSpan.FromMilliseconds(400),
            }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert — the Expires-only deletion and the Max-Age that serializes to 0 pass; the live cookie is held.
        fields.Select(field => field.Name).ShouldBe(["gone", "gone-too"]);
        fields.Single(field => field.Name == "gone-too").Get("Max-Age").ShouldBe("0");
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("live", CookiePolicyRejectionReason.ConsentRequired));
    }

    // ---- One Set-Cookie field line per cookie (RFC 6265 3) -----------------------------------------

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - RFC 6265 3: Each policy-issued cookie should travel in its own Set-Cookie field line")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task UseCookiePolicy_SeveralCookies_ShouldEmitOneFieldLinePerCookie(WebApplicationTestProtocol protocol)
    {
        // Arrange
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        factory.Application.UseCookiePolicy(options =>
        {
            options.TimeProvider = new FixedTimeProvider(CookiePolicyTestHost.Now);
            options.MinimumSameSitePolicy = HttpCookieSameSiteMode.Lax;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("a", "1", new HttpCookieOptions { Expires = CookiePolicyTestHost.Now.AddYears(2) }));
            context.Response.Cookies.Add(new HttpCookie("b", "2"));
            context.Response.Cookies.Add(new HttpCookie("c", "3", new HttpCookieOptions { SameSite = HttpCookieSameSiteMode.Strict }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert — three field lines, in append order; the Expires date's comma did not split a line.
        fields.Select(field => field.Name).ShouldBe(["a", "b", "c"]);
        fields[0].Get("Expires").ShouldBe(CookiePolicyTestHost.Now.AddDays(400).ToString("R", CultureInfo.InvariantCulture));
        fields.Select(field => field.Get("SameSite")).ShouldBe(["Lax", "Lax", "Strict"]);
    }

    private static WebApplicationTestFactory CreateFactoryAtFixedTime()
    {
        WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.TimeProvider = new FixedTimeProvider(CookiePolicyTestHost.Now));
        return factory;
    }

    private static async Task<IReadOnlyList<SetCookieField>> SendAsync(WebApplicationTestFactory factory)
    {
        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();
        return await CookiePolicyTestHost.SendAsync(client, cancellation.Token);
    }
}
