using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Rewrite.Tests;

/// <summary>
/// Rewriting inside a <c>Map(path)</c> branch (Web ADR 1, decision 3): the rules see the branch's effective
/// path, a rewrite publishes a matching <see cref="IWebPathBaseFeature"/> under the unchanged path base while
/// <c>Request.Path</c> is the path base and the rewritten path, redirects are relative to the path base, and
/// the branch's own view comes back when the rest of the branch returns.
/// </summary>
public class RewriteBranchTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: A rewrite in a branch should rewrite the effective path under the same path base")]
    public async Task UseRewrite_InPathBranch_ShouldRewriteEffectivePath()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.Map("/docs", branch =>
        {
            branch.UseRewrite(rules => rules.AddRewrite("^/old/(.*)$", "/new/$1"));
            branch.Run(probe.InvokeAsync);
        });

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/docs/old/intro"));

        // Assert
        probe.EffectivePath!.Value.Value.ShouldBe("/new/intro");
        probe.PathBase!.Value.Value.ShouldBe("/docs");
        probe.Path!.Value.Value.ShouldBe("/docs/new/intro");
        probe.Rewrite!.OriginalPath.Value.ShouldBe("/docs/old/intro");
        context.Features.Get<IWebPathBaseFeature>().ShouldBeNull();
        context.Features.Get<IWebRewriteFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: Rules in a branch should match the path below the prefix, not the full path")]
    public async Task UseRewrite_InPathBranch_ShouldMatchEffectivePathOnly()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.Map("/docs", branch =>
        {
            branch.UseRewrite(rules => rules.AddRewrite("^/docs/(.*)$", "/never/$1"));
            branch.Run(probe.InvokeAsync);
        });

        // Act
        await builder.SendAsync(new TestHttpContext("/docs/docs/x"));

        // Assert — "/docs/x" below the prefix matches; the full path's leading "/docs" is the base.
        probe.EffectivePath!.Value.Value.ShouldBe("/never/x");
        probe.Path!.Value.Value.ShouldBe("/docs/never/x");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: The branch's own view should be back for the middleware ahead of the rewrite")]
    public async Task UseRewrite_InPathBranch_ShouldRestoreBranchViewOnReturn()
    {
        // Arrange
        HttpPath? effectiveAfterNext = null;
        HttpPath? requestAfterNext = null;
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.Map("/docs", branch =>
        {
            branch.Use(async (IHttpContext context, WebApplicationMiddleware next) =>
            {
                await next(context);
                effectiveAfterNext = context.GetEffectivePath();
                requestAfterNext = context.Request.Path;
            });
            branch.UseRewrite(rules => rules.AddRewrite("^/a$", "/b"));
            branch.Run(probe.InvokeAsync);
        });

        // Act
        await builder.SendAsync(new TestHttpContext("/docs/a"));

        // Assert
        probe.EffectivePath!.Value.Value.ShouldBe("/b");
        effectiveAfterNext!.Value.Value.ShouldBe("/a");
        requestAfterNext!.Value.Value.ShouldBe("/docs/a");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: A nested branch should match the rewritten effective path")]
    public async Task UseRewrite_InPathBranch_ShouldFeedNestedBranch()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.Map("/api", api =>
        {
            api.UseRewrite(rules => rules.AddRewrite("^/latest/(.*)$", "/v2/$1"));
            api.Map("/v2", v2 => v2.Run(probe.InvokeAsync));
        });

        // Act
        await builder.SendAsync(new TestHttpContext("/api/latest/orders"));

        // Assert
        probe.PathBase!.Value.Value.ShouldBe("/api/v2");
        probe.EffectivePath!.Value.Value.ShouldBe("/orders");
        probe.Path!.Value.Value.ShouldBe("/api/v2/orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: A rewrite to the branch root should keep the path base")]
    public async Task UseRewrite_InPathBranchToRoot_ShouldKeepPathBase()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.Map("/docs", branch =>
        {
            branch.UseRewrite(rules => rules.AddRewrite("^/index\\.html$", "/"));
            branch.Run(probe.InvokeAsync);
        });

        // Act
        await builder.SendAsync(new TestHttpContext("/docs/index.html"));

        // Assert
        probe.EffectivePath!.Value.Value.ShouldBe("/");
        probe.PathBase!.Value.Value.ShouldBe("/docs");
        probe.Path!.Value.Value.ShouldBe("/docs/");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: An application-level rewrite should decide which branch runs")]
    public async Task UseRewrite_AheadOfBranch_ShouldSelectBranchByRewrittenPath()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/help/(.*)$", "/docs/$1"));
        builder.Map("/docs", branch => branch.Run(probe.InvokeAsync));

        // Act
        await builder.SendAsync(new TestHttpContext("/help/intro"));

        // Assert
        probe.PathBase!.Value.Value.ShouldBe("/docs");
        probe.EffectivePath!.Value.Value.ShouldBe("/intro");
        probe.Rewrite!.OriginalPath.Value.ShouldBe("/help/intro");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: A rewrite in a branch after an application-level one should keep the client's originals")]
    public async Task UseRewrite_ApplicationThenBranch_ShouldKeepClientOriginals()
    {
        // Arrange
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(rules => rules.AddRewrite("^/help/(.*)$", "/docs/$1"));
        builder.Map("/docs", branch =>
        {
            branch.UseRewrite(rules => rules.AddRewrite("^/intro$", "/getting-started"));
            branch.Run(probe.InvokeAsync);
        });

        // Act
        await builder.SendAsync(new TestHttpContext("/help/intro"));

        // Assert
        probe.Path!.Value.Value.ShouldBe("/docs/getting-started");
        probe.EffectivePath!.Value.Value.ShouldBe("/getting-started");
        probe.Rewrite!.OriginalPath.Value.ShouldBe("/help/intro");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: A redirect in a branch should be relative to the path base")]
    public async Task UseRewrite_RedirectInPathBranch_ShouldPrefixPathBase()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        builder.Map("/docs", branch =>
        {
            branch.UseRewrite(rules => rules
                .AddRedirect("^/old/(.*)$", "/new/$1", HttpStatusCode.MovedPermanently)
                .AddRedirect("^/elsewhere$", "https://example.org/", HttpStatusCode.Found)
                .AddRedirectToLowercase());
            branch.Run(_ => Task.CompletedTask);
        });

        // Act
        TestHttpContext moved = await builder.SendAsync(new TestHttpContext("/docs/old/intro", "v=1"));
        TestHttpContext absolute = await builder.SendAsync(new TestHttpContext("/docs/elsewhere"));
        TestHttpContext lowered = await builder.SendAsync(new TestHttpContext("/docs/Intro"));

        // Assert
        moved.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/docs/new/intro?v=1");
        absolute.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.org/");
        lowered.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/docs/intro");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Branch: A delegate rule in a branch should see the path base and the path below it")]
    public async Task UseRewrite_DelegateInPathBranch_ShouldSeeBaseAndEffectivePath()
    {
        // Arrange
        HttpPath? seenBase = null;
        HttpPath? seenPath = null;
        TestPipelineBuilder builder = new();
        builder.Map("/docs", branch =>
        {
            branch.UseRewrite(rules => rules.Add(context =>
            {
                seenBase = context.PathBase;
                seenPath = context.Path;
            }));
            branch.Run(_ => Task.CompletedTask);
        });

        // Act
        await builder.SendAsync(new TestHttpContext("/docs/guide"));

        // Assert
        seenBase!.Value.Value.ShouldBe("/docs");
        seenPath!.Value.Value.ShouldBe("/guide");
    }
}
