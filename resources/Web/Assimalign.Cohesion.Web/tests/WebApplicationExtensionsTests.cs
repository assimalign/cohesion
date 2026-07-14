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
/// registration surface.
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
}
