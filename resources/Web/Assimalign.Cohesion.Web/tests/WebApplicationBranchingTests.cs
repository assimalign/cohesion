using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Tests;

/// <summary>
/// Pipeline branching (#1056): <c>Map(path)</c>, <c>MapWhen</c>, <c>UseWhen</c>, <c>Run</c>, the path-base
/// view a path branch publishes, and the standard terminal every non-rejoining branch ends in.
/// </summary>
public class WebApplicationBranchingTests
{
    // ------------------------------------------------------------------ Map(path)

    [Theory(DisplayName = "Cohesion Test [Web] - Map: A path branch sees the path below its prefix and the prefix as the path base")]
    [InlineData("/static/app.js", "/app.js")]
    [InlineData("/STATIC/app.js", "/app.js")]
    [InlineData("/static", "/")]
    [InlineData("/static/", "/")]
    public async Task Map_MatchingPath_ShouldRunBranchWithEffectivePathAndBase(string requestPath, string expectedPath)
    {
        // Arrange
        TestPipelineBuilder builder = new();
        HttpPath? seenPath = null;
        HttpPath? seenBase = null;
        HttpPath? seenRequestPath = null;
        builder.Map("/static", branch => branch.Run(context =>
        {
            seenPath = context.GetEffectivePath();
            seenBase = context.GetPathBase();
            seenRequestPath = context.Request.Path;
            return Task.CompletedTask;
        }));

        // Act
        TestHttpContext context = await builder.SendAsync(HttpMethod.Get, requestPath);

        // Assert — the request itself is not rewritten; the branch reads the effective view.
        seenPath!.Value.Value.ShouldBe(expectedPath);
        seenBase!.Value.Value.ShouldBe("/static");
        seenRequestPath!.Value.Value.ShouldBe(requestPath);
        context.Features.Get<IWebPathBaseFeature>().ShouldBeNull(); // restored after the branch
        context.GetEffectivePath().Value.ShouldBe(requestPath);
    }

    [Theory(DisplayName = "Cohesion Test [Web] - Map: A path that only shares a prefix's characters skips the branch")]
    [InlineData("/staticx/app.js")]
    [InlineData("/other")]
    public async Task Map_NonMatchingPath_ShouldContinueMainPipeline(string requestPath)
    {
        // Arrange
        TestPipelineBuilder builder = new();
        bool branchRan = false;
        bool mainRan = false;
        builder.Map("/static", branch => branch.Run(_ => { branchRan = true; return Task.CompletedTask; }));
        builder.Run(_ => { mainRan = true; return Task.CompletedTask; });

        // Act
        await builder.SendAsync(HttpMethod.Get, requestPath);

        // Assert
        branchRan.ShouldBeFalse();
        mainRan.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Map: Nested path branches accumulate the path base")]
    public async Task Map_Nested_ShouldAccumulatePathBase()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        HttpPath? seenPath = null;
        HttpPath? seenBase = null;
        builder.Map("/api", api => api.Map("/v1", v1 => v1.Run(context =>
        {
            seenPath = context.GetEffectivePath();
            seenBase = context.GetPathBase();
            return Task.CompletedTask;
        })));

        // Act
        await builder.SendAsync(HttpMethod.Get, "/api/v1/orders");

        // Assert
        seenPath!.Value.Value.ShouldBe("/orders");
        seenBase!.Value.Value.ShouldBe("/api/v1");
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Map: A branch without a response ends in a 404 and does not rejoin")]
    public async Task Map_UnhandledInBranch_ShouldEndIn404WithoutRejoining()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        bool mainRan = false;
        builder.Map("/static", branch => branch.Use(next => next));
        builder.Run(_ => { mainRan = true; return Task.CompletedTask; });

        // Act
        TestHttpContext context = await builder.SendAsync(HttpMethod.Get, "/static/missing.js");

        // Assert
        mainRan.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Map: An endpoint selected before the branch runs at the branch's terminal")]
    public async Task Map_WithEndpointSelectedBeforeBranch_ShouldRunEndpointAtBranchTerminal()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        bool endpointRan = false;
        builder.Use(next => context =>
        {
            context.Features.Set<IWebEndpointFeature>(new TestEndpointFeature(_ => { endpointRan = true; return Task.CompletedTask; }));
            return next(context);
        });
        builder.Map("/api", branch => branch.Use(next => next));

        // Act
        await builder.SendAsync(HttpMethod.Get, "/api/orders");

        // Assert
        endpointRan.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Map: Component factories inside a branch receive the application context")]
    public async Task Map_ContextAwareMiddleware_ShouldReceiveApplicationContext()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        IWebApplicationContext? seen = null;
        builder.Map("/static", branch => branch.Use((IWebApplicationContext application, WebApplicationMiddleware next) =>
        {
            seen = application;
            return next;
        }));

        // Act
        builder.Build();

        // Assert — composed once, when the containing pipeline is built.
        seen.ShouldBeSameAs(builder.Context);
    }

    [Theory(DisplayName = "Cohesion Test [Web] - Map: The root or a non-origin path is rejected")]
    [InlineData("/")]
    [InlineData("*")]
    public void Map_InvalidPrefix_ShouldThrow(string prefix)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.Map(prefix, _ => { }));
    }

    // ------------------------------------------------------------------ MapWhen / UseWhen / Run

    [Fact(DisplayName = "Cohesion Test [Web] - MapWhen: A predicate branch runs only for matching requests and does not rejoin")]
    public async Task MapWhen_Predicate_ShouldBranchWithoutRejoining()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        List<string> calls = new();
        builder.MapWhen(context => context.Request.Method == HttpMethod.Post, branch => branch.Run(_ =>
        {
            calls.Add("branch");
            return Task.CompletedTask;
        }));
        builder.Run(_ => { calls.Add("main"); return Task.CompletedTask; });

        // Act
        await builder.SendAsync(HttpMethod.Post, "/x");
        await builder.SendAsync(HttpMethod.Get, "/x");

        // Assert
        calls.ShouldBe(new[] { "branch", "main" });
    }

    [Fact(DisplayName = "Cohesion Test [Web] - UseWhen: A conditional segment runs, then rejoins the main pipeline")]
    public async Task UseWhen_Predicate_ShouldRunSegmentThenRejoin()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        List<string> calls = new();
        builder.UseWhen(context => context.Request.Path.Value.StartsWith("/admin", StringComparison.Ordinal), segment => segment.Use(next => context =>
        {
            calls.Add("segment");
            return next(context);
        }));
        builder.Run(_ => { calls.Add("main"); return Task.CompletedTask; });

        // Act
        await builder.SendAsync(HttpMethod.Get, "/admin/users");
        await builder.SendAsync(HttpMethod.Get, "/public");

        // Assert
        calls.ShouldBe(new[] { "segment", "main", "main" });
    }

    [Fact(DisplayName = "Cohesion Test [Web] - UseWhen: A segment that short-circuits keeps the main pipeline from running")]
    public async Task UseWhen_SegmentShortCircuits_ShouldNotRejoin()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        bool mainRan = false;
        builder.UseWhen(_ => true, segment => segment.Run(context =>
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }));
        builder.Run(_ => { mainRan = true; return Task.CompletedTask; });

        // Act
        TestHttpContext context = await builder.SendAsync(HttpMethod.Get, "/x");

        // Assert
        mainRan.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Run: Terminal middleware answers and nothing after it runs")]
    public async Task Run_Terminal_ShouldStopThePipeline()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        bool laterRan = false;
        builder.Run(context =>
        {
            context.Response.StatusCode = HttpStatusCode.Accepted;
            return Task.CompletedTask;
        });
        builder.Use(next => context => { laterRan = true; return next(context); });

        // Act
        TestHttpContext context = await builder.SendAsync(HttpMethod.Get, "/x");

        // Assert
        laterRan.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // ------------------------------------------------------------------ terminal

    [Fact(DisplayName = "Cohesion Test [Web] - Terminal: An unhandled request gets a bodyless 404; a shaped response is left alone")]
    public async Task Terminal_Unhandled_ShouldSet404UnlessShaped()
    {
        // Arrange
        TestPipelineBuilder unhandled = new();
        TestPipelineBuilder shaped = new();
        shaped.Use(next => context =>
        {
            context.Response.StatusCode = HttpStatusCode.NoContent;
            return next(context);
        });

        // Act
        TestHttpContext first = await unhandled.SendAsync(HttpMethod.Get, "/x");
        TestHttpContext second = await shaped.SendAsync(HttpMethod.Get, "/x");

        // Assert
        first.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
