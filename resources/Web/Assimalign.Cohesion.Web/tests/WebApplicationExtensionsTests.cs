using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Tests;

/// <summary>
/// Coverage for the root pipeline-builder sugar: the inline
/// <c>Use(Func&lt;IHttpContext, WebApplicationMiddleware, Task&gt;)</c> adapter that
/// bridges application lambdas onto the core
/// <see cref="IWebApplicationPipelineBuilder.Use(Func{WebApplicationMiddleware, WebApplicationMiddleware})"/>
/// registration surface, the rejoining <c>UseWhen</c> segment and <c>Run</c> terminal middleware (#1056).
/// The branches that do not rejoin, <c>Map(path)</c> and <c>MapWhen</c>, moved to <c>Web.Routing</c> (#1379)
/// and are covered there.
/// </summary>
public class WebApplicationExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web] - Use: The inline-middleware adapter should register exactly one wrapped middleware and return the same builder")]
    public void Use_InlineMiddleware_ShouldRegisterOnceAndReturnSameBuilder()
    {
        // Arrange
        var builder = new RecordingPipelineBuilder();

        // Act
        IWebApplicationPipelineBuilder returned = builder.Use((context, next) => Task.CompletedTask);

        // Assert
        returned.ShouldBeSameAs(builder);
        builder.Registrations.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Use: The wrapped middleware should invoke the lambda with the pipeline's next delegate")]
    public async Task Use_InlineMiddleware_ShouldFlowContextAndNextThrough()
    {
        // Arrange — the lambda records what it receives and forwards to next; the
        // adapter under test never dereferences the context, so none is needed.
        var builder = new RecordingPipelineBuilder();
        var calls = new List<string>();

        builder.Use(async (context, next) =>
        {
            calls.Add("middleware");
            await next.Invoke(context);
        });

        WebApplicationMiddleware terminal = _ =>
        {
            calls.Add("terminal");
            return Task.CompletedTask;
        };

        // Act — compose the recorded registration around the terminal and run it.
        WebApplicationMiddleware composed = builder.Registrations[0].Invoke(terminal);
        await composed.Invoke(null!);

        // Assert
        calls.ShouldBe(new[] { "middleware", "terminal" });
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Use: A null builder or null middleware should throw at registration time")]
    public void Use_NullArguments_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder nullBuilder = null!;
        var builder = new RecordingPipelineBuilder();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => nullBuilder.Use((context, next) => Task.CompletedTask));
        Should.Throw<ArgumentNullException>(() => builder.Use((Func<IHttpContext, WebApplicationMiddleware, Task>)null!));
    }

    // ------------------------------------------------------------------ UseWhen / Run

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

    [Fact(DisplayName = "Cohesion Test [Web] - UseWhen: Component factories inside a segment receive the application context once")]
    public void UseWhen_ContextAwareMiddleware_ShouldReceiveApplicationContextOnce()
    {
        // Arrange
        TestPipelineBuilder builder = new();
        List<IWebApplicationContext> seen = new();
        builder.UseWhen(_ => true, segment => segment.Use((IWebApplicationContext application, WebApplicationMiddleware next) =>
        {
            seen.Add(application);
            return next;
        }));

        // Act
        builder.Build();

        // Assert — composed once, when the containing pipeline is built.
        seen.Count.ShouldBe(1);
        seen[0].ShouldBeSameAs(builder.Context);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - UseWhen: Null arguments throw at registration time")]
    public void UseWhen_NullArguments_ShouldThrow()
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.UseWhen(null!, _ => { }));
        Should.Throw<ArgumentNullException>(() => builder.UseWhen(_ => true, null!));
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
}
