using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The composed pipeline honors its execution token (#1051). Middleware delegates take no token and
/// observe cancellation through <see cref="IHttpContext.RequestCancelled"/>, so the pipeline gates
/// its own token at the start instead of dropping it.
/// </summary>
public class WebApplicationPipelineTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Pipeline: Middleware registration after build names the closed pipeline")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Use_AfterPipelineBuild_ShouldThrowWithCause(int overload)
    {
        // Arrange
        await using WebApplication application = WebApplication.CreateBuilder().Build();
        IWebApplicationPipelineBuilder pipeline = application;
        pipeline.Use(next => next);
        pipeline.Build();

        // Act
        var failure = Should.Throw<InvalidOperationException>(() =>
        {
            switch (overload)
            {
                case 0:
                    application.Use((context, next) => next(context));
                    break;
                case 1:
                    pipeline.Use(next => next);
                    break;
                default:
                    pipeline.Use((context, next) => next);
                    break;
            }
        });

        // Assert
        failure.Message.ShouldContain("pipeline", Case.Sensitive);
        failure.Message.ShouldContain("built", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Pipeline: A cancelled token keeps the middleware from starting")]
    public void ExecuteAsync_WithCancelledToken_ShouldReturnCanceledTaskWithoutRunningMiddleware()
    {
        // Arrange
        bool middlewareRan = false;
        WebApplicationPipeline pipeline = new(_ =>
        {
            middlewareRan = true;
            return Task.CompletedTask;
        });

        // Act
        Task execution = pipeline.ExecuteAsync(new FakeHttpContext(), new CancellationToken(canceled: true));

        // Assert
        execution.IsCanceled.ShouldBeTrue();
        middlewareRan.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Pipeline: An active token runs the middleware")]
    public async Task ExecuteAsync_WithActiveToken_ShouldRunMiddleware()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        IHttpContext? executed = null;
        WebApplicationPipeline pipeline = new(context =>
        {
            executed = context;
            return Task.CompletedTask;
        });
        FakeHttpContext context = new();

        // Act
        await pipeline.ExecuteAsync(context, cancellation.Token);

        // Assert
        executed.ShouldBeSameAs(context);
    }
}
