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
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests;

/// <summary>
/// How the policy intercepts cookie writes: it replaces the response cookie feature, judges cookies when
/// they are appended (including ones queued before it took over and ones appended on the way out),
/// composes with a feature installed before it, and keeps the collection's <c>Remove</c>/<c>Contains</c>
/// semantics for the instances the application appended.
/// </summary>
public class CookiePolicyInterceptionTests
{
    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: A cookie queued before the policy should be judged when the policy takes over")]
    public async Task UseCookiePolicy_CookieQueuedByEarlierMiddleware_ShouldBeJudged()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Cookies.Add(new HttpCookie("early", "1"));
            context.Response.Cookies.Add(new HttpCookie("tracking", "1"));
            await next.Invoke(context);
        });
        factory.Application.UseCookiePolicy(options =>
        {
            options.Secure = CookieSecurePolicy.Always;
            options.CheckConsentNeeded = _ => true;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, _ => { });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert — both were queued before the policy existed; both were judged when it took over.
        fields.ShouldBeEmpty();
        recorder.Rejections.Select(rejection => rejection.Name).ShouldBe(["early", "tracking"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: Queued cookies should keep their order when the policy adopts them")]
    public async Task UseCookiePolicy_AdoptedCookies_ShouldKeepTheirOrder()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Cookies.Add(new HttpCookie("first", "1"));
            context.Response.Cookies.Add(new HttpCookie("second", "1", new HttpCookieOptions { Secure = true }));
            context.Response.Cookies.Add(new HttpCookie("third", "1"));
            await next.Invoke(context);
        });
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("fourth", "1")));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.Select(field => field.Name).ShouldBe(["first", "second", "third", "fourth"]);
        fields.ShouldAllBe(field => field.Has("Secure"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: A cookie appended on the way out by earlier middleware should be judged")]
    public async Task UseCookiePolicy_CookieAppendedAfterNextByEarlierMiddleware_ShouldBeJudged()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.Use(async (context, next) =>
        {
            await next.Invoke(context);
            context.Response.Cookies.Add(new HttpCookie("late", "1"));
        });
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, _ => { });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Name.ShouldBe("late");
        field.Has("Secure").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: Remove and Contains should work with the instance the application appended")]
    public async Task Remove_OriginalInstanceOfARewrittenCookie_ShouldRemoveTheIssuedCookie()
    {
        // Arrange
        (bool ContainsBefore, bool Removed, bool ContainsAfter, int Count)? observed = null;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            IHttpCookieCollection cookies = context.Response.Cookies;
            HttpCookie appended = new("a", "1");
            cookies.Add(appended);
            bool containsBefore = cookies.Contains(appended);
            bool removed = cookies.Remove(appended);
            observed = (containsBefore, removed, cookies.Contains(appended), cookies.Count);
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        observed.ShouldBe((true, true, false, 0));
        fields.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: A dropped cookie should not be in the collection")]
    public async Task Contains_DroppedCookie_ShouldBeFalse()
    {
        // Arrange
        (bool Contains, bool Removed, int Count)? observed = null;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.CheckConsentNeeded = _ => true);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            IHttpCookieCollection cookies = context.Response.Cookies;
            HttpCookie dropped = new("tracking", "1");
            cookies.Add(dropped);
            observed = (cookies.Contains(dropped), cookies.Remove(dropped), cookies.Count);
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        observed.ShouldBe((false, false, 0));
        fields.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: The collection should enumerate the cookies as issued, without mutating the appended instance")]
    public async Task Add_RewrittenCookie_ShouldQueueACopyAndLeaveTheAppendedInstanceAlone()
    {
        // Arrange
        HttpCookie appended = new("a", "1");
        (bool QueuedSecure, bool AppendedSecure, bool SameInstance)? observed = null;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(appended);
            HttpCookie queued = context.Response.Cookies.Single();
            observed = (queued.Options.Secure, appended.Options.Secure, ReferenceEquals(queued, appended));
        });

        // Act
        await SendAsync(factory);

        // Assert
        observed.ShouldBe((true, false, false));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: A cookie instance shared across requests should not carry one request's decision into the next")]
    public async Task UseCookiePolicy_SharedCookieInstance_ShouldBeJudgedPerRequest()
    {
        // Arrange
        HttpCookie shared = new("shared", "1");
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context => context.Response.Cookies.Add(shared));

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act
        IReadOnlyList<SetCookieField> overHttps = await CookiePolicyTestHost.SendAsync(client, cancellation.Token, CookiePolicyTestHost.ForwardedHttps);
        IReadOnlyList<SetCookieField> overHttp = await CookiePolicyTestHost.SendAsync(client, cancellation.Token);

        // Assert
        overHttps.ShouldHaveSingleItem().Has("Secure").ShouldBeTrue();
        overHttp.ShouldHaveSingleItem().Has("Secure").ShouldBeFalse();
        shared.Options.Secure.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: A response cookie feature installed earlier should be composed underneath the policy")]
    public async Task UseCookiePolicy_ExistingResponseCookieFeature_ShouldBeComposedNotBypassed()
    {
        // Arrange
        RecordingResponseCookieFeature? recording = null;
        (bool ResolvedIsRecording, int FeatureCount)? observed = null;
        await using WebApplicationTestFactory factory = new();
        factory.Application.Use(async (context, next) =>
        {
            recording = new RecordingResponseCookieFeature(context.Response.Headers);
            context.Features.Set<IHttpResponseCookieFeature>(recording);
            await next.Invoke(context);
        });
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            observed = (
                ReferenceEquals(context.Features.Get<IHttpResponseCookieFeature>(), recording),
                context.Features.OfType<IHttpResponseCookieFeature>().Count());
            context.Response.Cookies.Add(new HttpCookie("a", "1"));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert — the policy is in charge, and the earlier feature still received the issued cookie.
        observed.ShouldBe((false, 1));
        HttpCookie stored = recording.ShouldNotBeNull().Added.ShouldHaveSingleItem();
        stored.Name.ShouldBe("a");
        stored.Options.Secure.ShouldBeTrue();
        fields.ShouldHaveSingleItem().Has("Secure").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: A Set-Cookie field written before the cookie collection exists should be judged when the collection is created")]
    public async Task UseCookiePolicy_RawSetCookieBeforeTheCollection_ShouldBeAdopted()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Headers[HttpHeaderKey.SetCookie] = "raw=1; Path=/";
            context.Response.Cookies.Add(new HttpCookie("a", "1"));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.Select(field => field.Name).ShouldBe(["raw", "a"]);
        fields.ShouldAllBe(field => field.Has("Secure"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Interception: A nested registration should add its rules and share the outer consent state")]
    public async Task UseCookiePolicy_NestedInABranch_ShouldComposeRulesAndShareConsent()
    {
        // Arrange
        int consentFeatures = 0;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.CheckConsentNeeded = _ => true);
        factory.Application.Map("/strict", branch =>
        {
            branch.UseCookiePolicy(options => options.MinimumSameSitePolicy = HttpCookieSameSiteMode.Strict);
            branch.Run(context =>
            {
                consentFeatures = context.Features.OfType<ICookieConsentFeature>().Count();
                context.Features.Get<ICookieConsentFeature>().ShouldNotBeNull().GrantConsent();
                context.Response.Cookies.Add(new HttpCookie("a", "1"));
                context.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });
        });

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act
        IReadOnlyList<SetCookieField> fields = await CookiePolicyTestHost.SendAsync(client, "/strict", cancellation.Token);

        // Assert — one consent state for the exchange; the grant satisfied both registrations, and the
        // branch's Strict floor applied to both cookies.
        consentFeatures.ShouldBe(1);
        fields.Select(field => field.Name).ShouldBe([CookiePolicyOptions.DefaultConsentCookieName, "a"]);
        fields.ShouldAllBe(field => field.Get("SameSite") == "Strict");
    }

    private static async Task<IReadOnlyList<SetCookieField>> SendAsync(WebApplicationTestFactory factory)
    {
        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();
        return await CookiePolicyTestHost.SendAsync(client, cancellation.Token);
    }
}
