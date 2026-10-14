using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Rewrite.Tests;

/// <summary>
/// How the rule engine evaluates the rules: registration order, each rule against the URL the earlier rules
/// left, the flows after a rewrite (continue, skip the remaining rules, restart), the bound on rule passes,
/// predicate, delegate and custom rules, and requests no rule applies to.
/// </summary>
public class RewriteRuleEvaluationTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: Each rule should see the URL the earlier rules rewrote")]
    public async Task UseRewrite_RulesInOrder_ShouldFeedEachRuleTheEarlierRewrite()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .AddRewrite("^/a$", "/b")
            .AddRewrite("^/b$", "/c")
            .AddRewrite("^/a$", "/never"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/a"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/c");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: A rule registered first should win over a later rule for the same URL")]
    public async Task UseRewrite_TwoRulesMatchSameUrl_ShouldApplyFirstThenEvaluateSecondAgainstItsResult()
    {
        // Arrange — both match /items; the first rewrites it away, so the second never sees /items.
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .AddRewrite("^/items$", "/first")
            .AddRewrite("^/items$", "/second"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/items"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/first");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: SkipRemainingRules should stop evaluation and keep the rewrite")]
    public async Task UseRewrite_SkipRemainingRulesFlow_ShouldStopEvaluation()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .AddRewrite("^/a$", "/b", RewriteFlow.SkipRemainingRules)
            .AddRewrite("^/b$", "/c"));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/a"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/b");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: A delegate rule can stop evaluation without rewriting")]
    public async Task UseRewrite_DelegateSkipsRemainingRules_ShouldLeaveUrlUntouched()
    {
        // Arrange — a guard that keeps /api out of the catch-all rewrite below it.
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .Add(context =>
            {
                if (context.Path.StartsWith(new HttpPath("/api/")))
                {
                    context.SkipRemainingRules();
                }
            })
            .AddRewrite("^/(.*)$", "/spa/index.html"));
        builder.Run(probe.InvokeAsync);
        TestHttpContext api = new("/api/orders");
        TestHttpContext page = new("/settings");

        // Act
        await builder.SendAsync(api);
        IHttpContext apiDownstream = probe.Context!;
        await builder.SendAsync(page);

        // Assert
        apiDownstream.ShouldBeSameAs(api);
        api.Features.Get<IWebRewriteFeature>().ShouldBeNull();
        probe.Path!.Value.Value.ShouldBe("/spa/index.html");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: A redirect after a rewrite should see the rewritten URL")]
    public async Task UseRewrite_RedirectAfterRewrite_ShouldMatchRewrittenPath()
    {
        // Arrange — documented ordering pitfall: register redirects ahead of rewrites, or a redirect exposes
        // the rewritten URL.
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .AddRewrite("^/old$", "/internal")
            .AddRedirect("^/internal$", "/exposed", HttpStatusCode.MovedPermanently));
        builder.Run(probe.InvokeAsync);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/old"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(301);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/exposed");
        probe.Invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: A restarting rule should run the rules again until the URL settles")]
    public async Task UseRewrite_RestartFlow_ShouldReevaluateFromFirstRule()
    {
        // Arrange — collapse repeated slashes one pass at a time.
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^(.*)//(.*)$", "$1/$2", RewriteFlow.Restart));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/a////b//c"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/a/b/c");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: A restart should run the earlier rules against the rewritten URL")]
    public async Task UseRewrite_RestartFlow_ShouldApplyEarlierRuleToRewrittenUrl()
    {
        // Arrange — the first rule only matches what the second rule produces.
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .AddRewrite("^/v2/(.*)$", "/api/$1")
            .AddRewrite("^/legacy/(.*)$", "/v2/$1", RewriteFlow.Restart));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/legacy/orders"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/api/orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Loop protection: Rules that restart on every pass should fail after the default bound")]
    public async Task UseRewrite_RestartLoop_ShouldThrowInvalidOperationAfterMaxPasses()
    {
        // Arrange
        int evaluations = 0;
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .Add(_ => evaluations++)
            .AddRewrite("^/(.*)$", "/x$1", RewriteFlow.Restart));
        builder.Run(_ => Task.CompletedTask);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => builder.SendAsync(new TestHttpContext("/loop")));

        // Assert
        evaluations.ShouldBe(10);
        exception.Message.ShouldContain("10 rule passes", Case.Sensitive);
        exception.Message.ShouldContain(nameof(RewriteOptions.MaxPasses), Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Loop protection: MaxPasses should bound the passes a request may take")]
    [InlineData(3, true)]
    [InlineData(2, false)]
    public async Task UseRewrite_MaxPasses_ShouldBoundRestarts(int maxPasses, bool settles)
    {
        // Arrange — "/a//b//c" needs two restarts, so three passes.
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules =>
        {
            rules.MaxPasses = maxPasses;
            rules.AddRewrite("^(.*)//(.*)$", "$1/$2", RewriteFlow.Restart);
        });
        builder.Run(probe.InvokeAsync);

        // Act
        Exception? exception = await Record.ExceptionAsync(() => builder.SendAsync(new TestHttpContext("/a//b//c")));

        // Assert
        if (settles)
        {
            exception.ShouldBeNull();
            probe.Path!.Value.Value.ShouldBe("/a/b/c");
        }
        else
        {
            exception.ShouldBeOfType<InvalidOperationException>();
            probe.Invocations.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Loop protection: MaxPasses below one should be rejected")]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxPasses_LessThanOne_ShouldThrow(int value)
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxPasses = value);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: A request no rule rewrites should continue with the context the middleware received")]
    public async Task UseRewrite_NoRuleMatches_ShouldPassOriginalContext()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/old$", "/new").AddRedirect("^/gone$", "/"));
        builder.Run(probe.InvokeAsync);
        TestHttpContext context = new("/current", "a=1");

        // Act
        await builder.SendAsync(context);

        // Assert
        probe.Context.ShouldBeSameAs(context);
        probe.Rewrite.ShouldBeNull();
        context.Response.StatusCode.Value.ShouldBe(200);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: A rewrite back to the original URL should not create a view")]
    public async Task UseRewrite_RewriteToSameUrl_ShouldPassOriginalContext()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/same$", "/same"));
        builder.Run(probe.InvokeAsync);
        TestHttpContext context = new("/same");

        // Act
        await builder.SendAsync(context);

        // Assert
        probe.Context.ShouldBeSameAs(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: OPTIONS * should never reach the rules")]
    public async Task UseRewrite_AsteriskFormRequest_ShouldSkipRules()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.Add(_ => throw new InvalidOperationException("A rule ran for OPTIONS *.")));
        builder.Run(probe.InvokeAsync);
        TestHttpContext context = new("*");
        context.Request.Method = HttpMethod.Options;

        // Act
        await builder.SendAsync(context);

        // Assert
        probe.Context.ShouldBeSameAs(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Rules: No rules should pass every request through")]
    public async Task UseRewrite_NoRules_ShouldPassThrough()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(_ => { });
        builder.Run(probe.InvokeAsync);
        TestHttpContext context = new("/anything");

        // Act
        await builder.SendAsync(context);

        // Assert
        probe.Context.ShouldBeSameAs(context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Predicate: A predicate rewrite should apply only when the predicate holds")]
    public async Task AddRewrite_Predicate_ShouldRewriteWhenPredicateHolds()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite(
            context => context.HttpContext.Request.Headers.ContainsKey("X-Mobile"),
            "/mobile?layout=compact"));
        builder.Run(probe.InvokeAsync);
        TestHttpContext mobile = new("/home", "a=1");
        mobile.Request.Headers["X-Mobile"] = "1";

        // Act
        await builder.SendAsync(mobile);
        HttpPath? mobilePath = probe.Path;
        IHttpQueryCollection? mobileQuery = probe.Query;
        await builder.SendAsync(new TestHttpContext("/home"));

        // Assert
        mobilePath!.Value.Value.ShouldBe("/mobile");
        mobileQuery!.Count.ShouldBe(1);
        mobileQuery["layout"].Value.ShouldBe("compact");
        probe.Path!.Value.Value.ShouldBe("/home");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Predicate: A predicate rule should see the URL as the earlier rules left it")]
    public async Task AddRedirect_Predicate_ShouldSeeRewrittenPath()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .AddRewrite("^/a$", "/b")
            .AddRedirect(context => context.Path.Value == "/b", "/target", HttpStatusCode.RedirectKeepVerb));
        builder.Run(_ => Task.CompletedTask);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/a"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(307);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/target");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Predicate: A predicate rule's target cannot substitute captures")]
    public void AddRewrite_PredicateTargetWithSubstitution_ShouldThrow()
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.AddRewrite(_ => true, "/items/$1"));
        Should.Throw<ArgumentException>(() => options.AddRedirect(_ => true, "/items/${id}"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Delegate: A delegate rule can answer the request and end the pipeline")]
    public async Task Add_DelegateEndsResponse_ShouldNotRunPipeline()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.Add(context =>
        {
            if (context.Path.Value == "/retired")
            {
                context.HttpContext.Response.StatusCode = 410;
                context.EndResponse();
            }
        }));
        builder.Run(probe.InvokeAsync);

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/retired"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(410);
        probe.Invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Delegate: A delegate rule can rewrite the path and the query")]
    public async Task Add_DelegateRewrite_ShouldRewritePathAndQuery()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.Add(context =>
            context.Rewrite(new HttpPath("/search"), new HttpQuery("q=" + context.Path.Value.TrimStart('/')).Parse())));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/cats", "page=2"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/search");
        probe.Query!["q"].Value.ShouldBe("cats");
        probe.Query.ContainsKey("page").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Delegate: A rule should take no action after it ended the exchange")]
    public async Task Add_DelegateActsAfterRedirect_ShouldThrowInvalidOperation()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.Add(context =>
        {
            context.Redirect("/elsewhere", HttpStatusCode.Found);
            context.Rewrite(new HttpPath("/other"));
        }));
        builder.Run(_ => Task.CompletedTask);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => builder.SendAsync(new TestHttpContext("/x")));
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Delegate: A redirect location must be a single, non-empty URL")]
    [InlineData("")]
    [InlineData("/a b")]
    [InlineData("/a\r\nSet-Cookie: x=1")]
    public async Task Add_DelegateRedirectInvalidLocation_ShouldThrowArgument(string location)
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.Add(context => context.Redirect(location, HttpStatusCode.Found)));
        builder.Run(_ => Task.CompletedTask);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() => builder.SendAsync(new TestHttpContext("/x")));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Delegate: A rewritten path must start with '/'")]
    public async Task Add_DelegateRewriteToAsterisk_ShouldThrowArgument()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.Add(context => context.Rewrite(new HttpPath("*"))));
        builder.Run(_ => Task.CompletedTask);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() => builder.SendAsync(new TestHttpContext("/x")));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Custom rule: An IRewriteRule should run in order with the built-in rules")]
    public async Task Add_CustomRule_ShouldReceiveRewrittenContext()
    {
        // Arrange
        TenantRule tenant = new();
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules
            .AddRewrite("^/t/([a-z]+)/(.*)$", "/$2?tenant=$1")
            .Add(tenant));
        builder.Run(probe.InvokeAsync);

        // Act
        await builder.SendAsync(new TestHttpContext("/t/acme/orders"));

        // Assert
        tenant.SeenPath.ShouldBe("/orders");
        tenant.SeenTenant.ShouldBe("acme");
        probe.Path!.Value.Value.ShouldBe("/orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Composition: A null configure callback or rule should be rejected")]
    public void UseRewrite_NullArguments_ShouldThrow()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => builder.UseRewrite(null!));
        Should.Throw<ArgumentNullException>(() => options.Add((IRewriteRule)null!));
        Should.Throw<ArgumentNullException>(() => options.Add((Action<IRewriteContext>)null!));
        Should.Throw<ArgumentNullException>(() => options.AddRewrite((string)null!, "/x"));
        Should.Throw<ArgumentNullException>(() => options.AddRewrite((Func<IRewriteContext, bool>)null!, "/x"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Composition: Undefined flow and match target values should be rejected")]
    public void AddRewrite_UndefinedEnumValues_ShouldThrow()
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRewrite("^/a$", "/b", (RewriteFlow)42));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRewrite("^/a$", "/b", RewriteFlow.Continue, (RewriteMatchTarget)42));
    }

    private sealed class TenantRule : IRewriteRule
    {
        public string? SeenPath { get; private set; }

        public string? SeenTenant { get; private set; }

        public void Apply(IRewriteContext context)
        {
            SeenPath = context.Path.Value;
            SeenTenant = context.Query["tenant"].Value;
        }
    }
}
