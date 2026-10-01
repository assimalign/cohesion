using System;
using System.Collections.Generic;
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
/// Consent gating, end to end: while a request needs consent and has none, only essential cookies and
/// deletions are issued; consent is read from the consent cookie and changed through
/// <see cref="ICookieConsentFeature"/>.
/// </summary>
public class CookiePolicyConsentTests
{
    private const string consentCookie = CookiePolicyOptions.DefaultConsentCookieName;

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: Without consent, a non-essential cookie should be dropped and an essential one issued")]
    public async Task UseCookiePolicy_ConsentRequiredAndAbsent_ShouldIssueOnlyEssentialCookies()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(recorder);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("tracking", "1"));
            context.Response.Cookies.Add(new HttpCookie("cart", "1", new HttpCookieOptions { IsEssential = true }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Name.ShouldBe("cart");
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("tracking", CookiePolicyRejectionReason.ConsentRequired));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: Without consent, a deletion should still be issued")]
    public async Task UseCookiePolicy_ConsentRequiredAndAbsent_ShouldIssueDeletions()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(recorder);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("tracking", string.Empty, new HttpCookieOptions
            {
                Expires = DateTimeOffset.UnixEpoch,
                MaxAge = TimeSpan.Zero,
            })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Name.ShouldBe("tracking");
        field.Get("Max-Age").ShouldBe("0");
        recorder.Rejections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: A request carrying the consent cookie should get non-essential cookies")]
    public async Task UseCookiePolicy_RequestCarriesConsentCookie_ShouldIssueNonEssentialCookies()
    {
        // Arrange
        bool? hasConsent = null;
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(recorder);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            hasConsent = context.Features.Get<ICookieConsentFeature>()?.HasConsent;
            context.Response.Cookies.Add(new HttpCookie("tracking", "1"));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, ("Cookie", $"{consentCookie}=yes"));

        // Assert
        hasConsent.ShouldBe(true);
        fields.ShouldHaveSingleItem().Name.ShouldBe("tracking");
        recorder.Rejections.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: A consent cookie with any other value should not count as consent")]
    [InlineData("no")]
    [InlineData("YES")]
    [InlineData("")]
    public async Task UseCookiePolicy_ConsentCookieWithAnotherValue_ShouldNotCountAsConsent(string value)
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(recorder);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("tracking", "1")));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, ("Cookie", $"{consentCookie}={value}"));

        // Assert
        fields.ShouldBeEmpty();
        recorder.Contains("tracking", CookiePolicyRejectionReason.ConsentRequired).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: GrantConsent should issue the consent cookie and let later non-essential cookies through")]
    public async Task GrantConsent_DuringTheExchange_ShouldIssueConsentCookieAndLaterCookies()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(recorder);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("early", "1"));
            context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull().GrantConsent();
            context.Response.Cookies.Add(new HttpCookie("late", "1"));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert — "early" was judged before the grant; decisions are made when a cookie is appended.
        fields.Select(field => field.Name).ShouldBe([consentCookie, "late"]);
        SetCookieField consent = fields[0];
        consent.Value.ShouldBe("yes");
        consent.Get("Path").ShouldBe("/");
        consent.Get("Max-Age").ShouldBe("31536000");
        consent.Get("SameSite").ShouldBe("Lax");
        consent.Has("HttpOnly").ShouldBeFalse();
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("early", CookiePolicyRejectionReason.ConsentRequired));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: Granting consent twice should issue one consent cookie")]
    public async Task GrantConsent_CalledTwice_ShouldIssueOneConsentCookie()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory(new RejectionRecorder());
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            ICookieConsentFeature consent = context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull();
            consent.GrantConsent();
            consent.GrantConsent();
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Name.ShouldBe(consentCookie);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: Granting consent that the request already carries should issue nothing")]
    public async Task GrantConsent_RequestAlreadyHasConsent_ShouldIssueNoConsentCookie()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory(new RejectionRecorder());
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull().GrantConsent());

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, ("Cookie", $"{consentCookie}=yes"));

        // Assert
        fields.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: WithdrawConsent should delete the consent cookie and hold back later non-essential cookies")]
    public async Task WithdrawConsent_DuringTheExchange_ShouldDeleteConsentCookieAndDropLaterCookies()
    {
        // Arrange
        bool? canTrackAfter = null;
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(recorder);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            ICookieConsentFeature consent = context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull();
            consent.WithdrawConsent();
            canTrackAfter = consent.CanTrack;
            context.Response.Cookies.Add(new HttpCookie("tracking", "1"));
            context.Response.Cookies.Add(new HttpCookie("cart", "1", new HttpCookieOptions { IsEssential = true }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, ("Cookie", $"{consentCookie}=yes"));

        // Assert
        canTrackAfter.ShouldBe(false);
        fields.Select(field => field.Name).ShouldBe([consentCookie, "cart"]);
        SetCookieField deletion = fields[0];
        deletion.Value.ShouldBeEmpty();
        deletion.Get("Max-Age").ShouldBe("0");
        deletion.Get("Expires").ShouldBe("Thu, 01 Jan 1970 00:00:00 GMT");
        deletion.Get("Path").ShouldBe("/");
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("tracking", CookiePolicyRejectionReason.ConsentRequired));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: A grant followed by a withdrawal should emit only the deletion")]
    public async Task WithdrawConsent_AfterGrantInTheSameExchange_ShouldReplaceTheQueuedConsentCookie()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory(new RejectionRecorder());
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            ICookieConsentFeature consent = context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull();
            consent.GrantConsent();
            consent.WithdrawConsent();
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Name.ShouldBe(consentCookie);
        field.Get("Max-Age").ShouldBe("0");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: Withdrawing consent that was never given should issue nothing")]
    public async Task WithdrawConsent_WithoutConsent_ShouldIssueNothing()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory(new RejectionRecorder());
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull().WithdrawConsent());

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: A granted consent should round-trip through a client's cookie store")]
    public async Task GrantConsent_ThenNextRequest_ShouldCarryConsentForward()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory(new RejectionRecorder());
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            if (context.Request.Path.Value == "/consent")
            {
                context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull().GrantConsent();
                return;
            }

            context.Response.Cookies.Add(new HttpCookie("tracking", "1"));
        });

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act — the client's cookie store sends the consent cookie back, as a browser would.
        IReadOnlyList<SetCookieField> before = await CookiePolicyTestHost.SendAsync(client, "/", cancellation.Token);
        IReadOnlyList<SetCookieField> grant = await CookiePolicyTestHost.SendAsync(client, "/consent", cancellation.Token);
        IReadOnlyList<SetCookieField> after = await CookiePolicyTestHost.SendAsync(client, "/", cancellation.Token);

        // Assert
        before.ShouldBeEmpty();
        grant.ShouldHaveSingleItem().Name.ShouldBe(consentCookie);
        after.ShouldHaveSingleItem().Name.ShouldBe("tracking");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: The consent predicate should decide per request and run at most once per exchange")]
    public async Task CheckConsentNeeded_PerRequest_ShouldDecideOncePerExchange()
    {
        // Arrange
        int evaluations = 0;
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.CheckConsentNeeded = context =>
            {
                Interlocked.Increment(ref evaluations);
                return context.Request.Headers.TryGetValue(new HttpHeaderKey("X-Region"), out HttpHeaderValue region)
                    && region.Value == "eu";
            };
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("a", "1"));
            context.Response.Cookies.Add(new HttpCookie("b", "1"));
            _ = context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull().IsConsentNeeded;
        });

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act
        IReadOnlyList<SetCookieField> elsewhere = await CookiePolicyTestHost.SendAsync(client, cancellation.Token, ("X-Region", "us"));
        IReadOnlyList<SetCookieField> inEurope = await CookiePolicyTestHost.SendAsync(client, cancellation.Token, ("X-Region", "eu"));

        // Assert
        elsewhere.Select(field => field.Name).ShouldBe(["a", "b"]);
        inEurope.ShouldBeEmpty();
        evaluations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: Without a predicate consent should not be needed and every cookie may be issued")]
    public async Task UseCookiePolicy_NoConsentPredicate_ShouldNotNeedConsent()
    {
        // Arrange
        (bool IsConsentNeeded, bool HasConsent, bool CanTrack)? state = null;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            ICookieConsentFeature consent = context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull();
            state = (consent.IsConsentNeeded, consent.HasConsent, consent.CanTrack);
            context.Response.Cookies.Add(new HttpCookie("tracking", "1"));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        state.ShouldBe((false, false, true));
        fields.ShouldHaveSingleItem().Name.ShouldBe("tracking");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: The consent feature should be absent when the policy is not registered")]
    public async Task ConsentFeature_WithoutUseCookiePolicy_ShouldBeAbsent()
    {
        // Arrange
        bool? present = null;
        await using WebApplicationTestFactory factory = new();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            present = context.Features.Get<ICookieConsentFeature>() is not null);

        // Act
        await SendAsync(factory);

        // Assert
        present.ShouldBe(false);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: A consent feature the application installs first should decide consent in place of the policy's own")]
    public async Task UseCookiePolicy_ApplicationConsentFeatureInstalledFirst_ShouldBeHonored()
    {
        // Arrange — the policy's own predicate would never require consent; the application's feature does
        // whenever the request carries Sec-GPC: 1.
        int consentFeatures = 0;
        bool? resolvedIsApplications = null;
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.Use(async (context, next) =>
        {
            context.Features.Set<ICookieConsentFeature>(new GlobalPrivacyControlConsentFeature(context));
            await next.Invoke(context);
        });
        factory.Application.UseCookiePolicy(options =>
        {
            options.CheckConsentNeeded = _ => false;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            consentFeatures = context.Features.OfType<ICookieConsentFeature>().Count();
            resolvedIsApplications = context.Features.Get<ICookieConsentFeature>() is GlobalPrivacyControlConsentFeature;
            context.Response.Cookies.Add(new HttpCookie("tracking", "1"));
        });

        // Act
        IReadOnlyList<SetCookieField> optedOut = await SendAsync(factory, ("Sec-GPC", "1"));
        IReadOnlyList<SetCookieField> notOptedOut = await SendAsync(factory);

        // Assert
        optedOut.ShouldBeEmpty();
        notOptedOut.ShouldHaveSingleItem().Name.ShouldBe("tracking");
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("tracking", CookiePolicyRejectionReason.ConsentRequired));
        consentFeatures.ShouldBe(1);
        resolvedIsApplications.ShouldBe(true);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: A custom consent cookie name, value and template should be honored")]
    public async Task UseCookiePolicy_CustomConsentCookie_ShouldBeIssuedAndRead()
    {
        // Arrange
        bool? hasConsent = null;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.CheckConsentNeeded = _ => true;
            options.ConsentCookieName = "gdpr";
            options.ConsentCookieValue = "accepted";
            options.ConsentCookie.MaxAge = TimeSpan.FromDays(180);
            options.ConsentCookie.SameSite = HttpCookieSameSiteMode.Strict;
            options.ConsentCookie.IsEssential = false;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            ICookieConsentFeature consent = context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull();
            hasConsent = consent.HasConsent;
            consent.GrantConsent();
        });

        // Act — the request carries the default consent cookie, which this policy does not recognize.
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, ("Cookie", $"{consentCookie}=yes"));

        // Assert — and the consent cookie is issued although its template said not essential.
        hasConsent.ShouldBe(false);
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Raw.ShouldBe("gdpr=accepted; Path=/; Max-Age=15552000; SameSite=Strict");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Consent: The policy's floors should apply to the consent cookie")]
    public async Task GrantConsent_UnderHttpOnlyAndSecureFloors_ShouldHardenTheConsentCookie()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.CheckConsentNeeded = _ => true;
            options.HttpOnly = CookieHttpOnlyPolicy.Always;
            options.Secure = CookieSecurePolicy.Always;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull().GrantConsent());

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Has("Secure").ShouldBeTrue();
        field.Has("HttpOnly").ShouldBeTrue();
    }

    private static WebApplicationTestFactory CreateFactory(RejectionRecorder recorder)
    {
        WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.CheckConsentNeeded = _ => true;
            options.OnRejected = recorder.Record;
        });
        return factory;
    }

    private static async Task<IReadOnlyList<SetCookieField>> SendAsync(
        WebApplicationTestFactory factory,
        params (string Name, string Value)[] headers)
    {
        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();
        return await CookiePolicyTestHost.SendAsync(client, cancellation.Token, headers);
    }
}
