using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// Pipeline branches that do not rejoin (#1056, moved from the Web root by #1379): <c>Map(path)</c>,
/// <c>MapWhen</c>, the path-base view a path branch publishes, and the standard terminal every
/// non-rejoining branch ends in. The root's <c>UseWhen</c> and <c>Run</c> are covered in the root's tests.
/// </summary>
public class WebApplicationBranchingTests
{
    // ------------------------------------------------------------------ Map(path)

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Map: A path branch sees the path below its prefix and the prefix as the path base")]
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

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Map: A path that only shares a prefix's characters skips the branch")]
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

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: Nested path branches accumulate the path base")]
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

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: A branch without a response ends in a 404 and does not rejoin")]
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

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: An endpoint selected before the branch runs at the branch's terminal")]
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

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: Component factories inside a branch receive the application context")]
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

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Map: The root or a non-origin path is rejected")]
    [InlineData("/")]
    [InlineData("*")]
    public void Map_InvalidPrefix_ShouldThrow(string prefix)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => builder.Map(prefix, _ => { }));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Map: A branch that throws restores the enclosing path base")]
    public async Task Map_BranchThrows_ShouldRestoreEnclosingPathBase()
    {
        // Arrange — the outer branch observes the view after its inner branch faulted.
        TestPipelineBuilder builder = new();
        HttpPath? baseAfterInnerFault = null;
        builder.Map("/api", api =>
        {
            api.Use(next => async context =>
            {
                await Should.ThrowAsync<InvalidOperationException>(() => next(context));
                baseAfterInnerFault = context.GetPathBase();
            });
            api.Map("/v1", v1 => v1.Run(_ => throw new InvalidOperationException("branch fault")));
        });

        // Act
        TestHttpContext context = await builder.SendAsync(HttpMethod.Get, "/api/v1/orders");

        // Assert
        baseAfterInnerFault!.Value.Value.ShouldBe("/api");
        context.Features.Get<IWebPathBaseFeature>().ShouldBeNull();
    }

    // ------------------------------------------------------------------ MapWhen

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapWhen: A predicate branch runs only for matching requests and does not rejoin")]
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

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - MapWhen: A branch without a response ends in the terminal and does not rejoin")]
    public async Task MapWhen_UnhandledInBranch_ShouldEndInTerminalWithoutRejoining()
    {
        // Arrange — an endpoint selected before the branch runs at the branch's terminal; without one, a 404.
        TestPipelineBuilder builder = new();
        bool mainRan = false;
        bool endpointRan = false;
        builder.Use(next => context =>
        {
            if (context.Request.Path.Value == "/selected")
            {
                context.Features.Set<IWebEndpointFeature>(new TestEndpointFeature(_ => { endpointRan = true; return Task.CompletedTask; }));
            }

            return next(context);
        });
        builder.MapWhen(_ => true, branch => branch.Use(next => next));
        builder.Run(_ => { mainRan = true; return Task.CompletedTask; });

        // Act
        TestHttpContext unhandled = await builder.SendAsync(HttpMethod.Get, "/other");
        TestHttpContext selected = await builder.SendAsync(HttpMethod.Get, "/selected");

        // Assert
        mainRan.ShouldBeFalse();
        unhandled.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        endpointRan.ShouldBeTrue();
        selected.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    // ------------------------------------------------------------------ terminal

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Terminal: An unhandled request gets a bodyless 404; a shaped response is left alone")]
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
