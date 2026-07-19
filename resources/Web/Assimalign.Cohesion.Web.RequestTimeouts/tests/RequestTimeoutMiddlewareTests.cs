using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Tests;

/// <summary>
/// Middleware-level coverage for <c>UseRequestTimeouts</c> over the pipeline harness: expiry
/// translation (504 / problem payload / custom writer / started-response abort), endpoint-policy
/// precedence at the route-match seam, the per-exchange feature (disable / re-arm), timeout
/// attribution against client aborts, and the injected <see cref="TimeProvider"/>.
/// </summary>
public class RequestTimeoutMiddlewareTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ArmedTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NeverInTestBudget = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan PastArmedTimeout = TimeSpan.FromMilliseconds(450);

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - UseRequestTimeouts: Should throw on a null pipeline builder")]
    public void UseRequestTimeouts_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder builder = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.UseRequestTimeouts());
        Should.Throw<ArgumentNullException>(() => builder.UseRequestTimeouts(TimeSpan.FromSeconds(1)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: Global policy expiry should write 504 and cancel downstream work")]
    public async Task InvokeAsync_GlobalPolicyExpires_ShouldWrite504AndCancelDownstreamWork()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();
        CancellationToken observedToken = default;

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = ArmedTimeout },
            async ctx =>
            {
                observedToken = ctx.RequestCancelled;
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled);
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        observedToken.IsCancellationRequested.ShouldBeTrue();
        context.CancelRequested.ShouldBeFalse();
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: Expiry should ride the injected TimeProvider, not wall time")]
    public async Task InvokeAsync_InjectedTimeProvider_ShouldExpireOnlyWhenClockAdvances()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        ManualTimeProvider timeProvider = new();
        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options =>
            {
                options.TimeProvider = timeProvider;
                options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = NeverInTestBudget };
            },
            ctx => Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled));

        // Act
        Task execution = pipeline.ExecuteAsync(context, cancellationToken);

        await Task.Delay(100, cancellationToken);
        execution.IsCompleted.ShouldBeFalse();

        timeProvider.Advance(NeverInTestBudget);
        await execution.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: An endpoint policy shorter than the global default should win")]
    public async Task InvokeAsync_EndpointPolicyShorterThanGlobal_ShouldTimeoutOnEndpointPolicy()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = NeverInTestBudget },
            async ctx =>
            {
                // What UseRouting does between matching and dispatching: publish the match (with
                // its endpoint metadata) on the context's feature collection, then run the handler.
                ctx.Features.Set<Routing.IRouteMatchFeature>(new FakeRouteMatchFeature(new RequestTimeoutMetadata(ArmedTimeout)));
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled);
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: A disabled endpoint policy should run past the global timeout")]
    public async Task InvokeAsync_EndpointPolicyDisabled_ShouldRunPastGlobalTimeout()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = ArmedTimeout },
            async ctx =>
            {
                ctx.Features.Set<Routing.IRouteMatchFeature>(new FakeRouteMatchFeature(RequestTimeoutMetadata.Disabled));
                await Task.Delay(PastArmedTimeout, ctx.RequestCancelled);
                ctx.Response.StatusCode = HttpStatusCode.Ok;
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: An endpoint policy longer than the global default should replace its timer")]
    public async Task InvokeAsync_EndpointPolicyLongerThanGlobal_ShouldReplaceGlobalTimer()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = ArmedTimeout },
            async ctx =>
            {
                ctx.Features.Set<Routing.IRouteMatchFeature>(new FakeRouteMatchFeature(new RequestTimeoutMetadata(NeverInTestBudget)));
                await Task.Delay(PastArmedTimeout, ctx.RequestCancelled);
                ctx.Response.StatusCode = HttpStatusCode.Ok;
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert — had the global 100ms timer stayed armed, the 450ms handler would have been cancelled.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - Disable: The per-exchange feature should disarm the timeout")]
    public async Task InvokeAsync_FeatureDisable_ShouldDisarmForTheExchange()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = ArmedTimeout },
            async ctx =>
            {
                IRequestTimeoutFeature feature = ctx.Features.Get<IRequestTimeoutFeature>()!;
                feature.Disable();

                await Task.Delay(PastArmedTimeout, ctx.RequestCancelled);
                ctx.Response.StatusCode = HttpStatusCode.Ok;
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - SetTimeout: A handler-armed timeout with no configured policy should answer 504")]
    public async Task InvokeAsync_FeatureSetTimeout_WithoutConfiguredPolicy_ShouldArmAndAnswer504()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            configure: null,
            async ctx =>
            {
                IRequestTimeoutFeature feature = ctx.Features.Get<IRequestTimeoutFeature>()!;
                feature.SetTimeout(ArmedTimeout);

                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled);
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: A client abort should propagate and never be labeled a timeout")]
    public async Task InvokeAsync_ClientAbort_ShouldPropagateCancellationWithout504()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = NeverInTestBudget },
            async ctx =>
            {
                context.AbortClient();
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled);
            });

        // Act / Assert — the unwind reaches the server loop unchanged (its clean-drain path).
        await Should.ThrowAsync<OperationCanceledException>(
            pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));

        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.CancelRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: A started response should be aborted at the protocol layer instead of written")]
    public async Task InvokeAsync_ResponseAlreadyStarted_ShouldAbortExchangeInsteadOfWriting504()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(hasStarted: true));

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = ArmedTimeout },
            ctx => Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled));

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert — headers are on the wire, so the status must not be rewritten; the exchange is
        // cancelled instead (transport reset: h2/h3 stream reset, h1 truncate-and-close).
        context.CancelRequested.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: A problem-details policy should write problem+json and reset the staged response")]
    public async Task InvokeAsync_ProblemDetailsPolicy_ShouldWriteProblemPayloadAndResetStagedResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = ArmedTimeout,
                WriteProblemDetails = true,
            },
            async ctx =>
            {
                // Stage a partial response before timing out; the timeout answer must replace it.
                ctx.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
                await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("partial-body"), ctx.RequestCancelled);
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled);
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        context.Response.Headers[HttpHeaderKey.ContentType].ToString().ShouldBe("application/problem+json");

        string body = context.ReadResponseBody();
        body.ShouldContain("\"status\":504", Case.Sensitive);
        body.ShouldContain("Gateway Timeout", Case.Sensitive);
        body.ShouldNotContain("partial-body");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: A custom WriteResponse handler should own the timeout response")]
    public async Task InvokeAsync_CustomWriteResponse_ShouldOwnTheTimeoutResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = ArmedTimeout,
                WriteResponse = async ctx =>
                {
                    ctx.Response.StatusCode = HttpStatusCode.ServiceUnavailable;
                    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("custom-timeout"), CancellationToken.None);
                },
            },
            ctx => Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled));

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        context.ReadResponseBody().ShouldBe("custom-timeout");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: A handler that completes despite the timeout should keep its response")]
    public async Task InvokeAsync_HandlerCompletesDespiteTimeout_ShouldKeepHandlerResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = ArmedTimeout },
            async ctx =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestCancelled);
                }
                catch (OperationCanceledException)
                {
                    // The handler owns the cancellation and still produces a response.
                }

                ctx.Response.StatusCode = HttpStatusCode.Ok;
                await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("handled"), CancellationToken.None);
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert — only an expiry-attributable unwind is converted; a completed handler wins.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.ReadResponseBody().ShouldBe("handled");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - InvokeAsync: With no policies the request should pass through and the feature should be cleaned up")]
    public async Task InvokeAsync_NoPolicies_ShouldPassThroughAndCleanUpFeature()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using TimeoutTestContext context = new();
        bool featurePresentDuringRequest = false;

        IWebApplicationPipeline pipeline = BuildPipeline(
            configure: null,
            ctx =>
            {
                featurePresentDuringRequest = ctx.Features.Get<IRequestTimeoutFeature>() is not null;
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        // Act
        await pipeline.ExecuteAsync(context, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        featurePresentDuringRequest.ShouldBeTrue();
        context.Features.Get<IRequestTimeoutFeature>().ShouldBeNull();
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
    }

    private static IWebApplicationPipeline BuildPipeline(
        Action<RequestTimeoutOptions>? configure,
        WebApplicationMiddleware terminal)
    {
        TestPipelineBuilder builder = new();
        builder.UseRequestTimeouts(configure);
        builder.Use(next => context => terminal.Invoke(context));

        return builder.Build();
    }
}
