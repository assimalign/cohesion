using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Routing;

using Shouldly;

using Xunit;

using IPAddress = System.Net.IPAddress;

namespace Assimalign.Cohesion.Web.RateLimiting.Tests;

/// <summary>
/// Middleware-level coverage for <c>UseRateLimiting</c> over the pipeline harness: the global gate
/// (admit / 429 / Retry-After / OnRejected / OnDecision / custom status), the per-endpoint gate at the
/// route-match seam (named / inline / disabled / unknown policy), forwarded-composing client-address
/// partitioning, the started-response abort, and the request-lifetime permit hold.
/// </summary>
public class RateLimitingMiddlewareTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - UseRateLimiting: Should throw on a null pipeline builder")]
    public void UseRateLimiting_NullBuilder_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipelineBuilder builder = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.UseRateLimiting());
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: With no policies the request should pass through and the feature should be cleaned up")]
    public async Task InvokeAsync_NoPolicies_ShouldPassThroughAndCleanUpFeature()
    {
        // Arrange
        bool featurePresent = false;
        bool acquiredDuringRequest = false;

        IWebApplicationPipeline pipeline = BuildPipeline(
            configure: null,
            ctx =>
            {
                IRateLimitingFeature? feature = ctx.Features.Get<IRateLimitingFeature>();
                featurePresent = feature is not null;
                acquiredDuringRequest = feature?.IsAcquired ?? false;
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        await using RateLimitTestContext context = new();

        // Act
        await ExecuteAsync(pipeline, context);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        featurePresent.ShouldBeTrue();
        acquiredDuringRequest.ShouldBeTrue();
        context.Features.Get<IRateLimitingFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A global policy with a free permit should admit the request")]
    public async Task InvokeAsync_GlobalPolicyAdmits_ShouldRunHandler()
    {
        // Arrange
        bool handlerRan = false;
        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.GlobalPolicy = TestPolicies.FixedWindowSingle(),
            ctx =>
            {
                handlerRan = true;
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        await using RateLimitTestContext context = new();

        // Act
        await ExecuteAsync(pipeline, context);

        // Assert
        handlerRan.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: An exhausted global policy should answer 429 and not run the handler")]
    public async Task InvokeAsync_GlobalPolicyExhausted_ShouldReject429AndSkipHandler()
    {
        // Arrange
        int handlerInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.GlobalPolicy = TestPolicies.FixedWindowSingle(),
            ctx =>
            {
                handlerInvocations++;
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        // Act — the second request in the same window has no permit left.
        await using RateLimitTestContext first = new();
        await ExecuteAsync(pipeline, first);

        await using RateLimitTestContext second = new();
        await ExecuteAsync(pipeline, second);

        // Assert
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        handlerInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A window rejection should carry a Retry-After header from the lease")]
    public async Task InvokeAsync_WindowRejection_ShouldWriteRetryAfterFromLease()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.GlobalPolicy = TestPolicies.FixedWindowSingle(),
            ctx => Task.CompletedTask);

        // Act
        await using RateLimitTestContext first = new();
        await ExecuteAsync(pipeline, first);

        await using RateLimitTestContext second = new();
        await ExecuteAsync(pipeline, second);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        second.Response.Headers.ContainsKey(HttpHeaderKey.RetryAfter).ShouldBeTrue();
        second.Response.Headers[HttpHeaderKey.RetryAfter].ToString().ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A custom rejection status should replace the default 429")]
    public async Task InvokeAsync_CustomRejectionStatus_ShouldReplaceDefault()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(
            options =>
            {
                options.GlobalPolicy = TestPolicies.FixedWindowSingle();
                options.RejectionStatusCode = HttpStatusCode.ServiceUnavailable;
            },
            ctx => Task.CompletedTask);

        // Act
        await using RateLimitTestContext first = new();
        await ExecuteAsync(pipeline, first);

        await using RateLimitTestContext second = new();
        await ExecuteAsync(pipeline, second);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: The OnRejected hook should own the rejection response")]
    public async Task InvokeAsync_OnRejectedHook_ShouldOwnResponse()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(
            options =>
            {
                options.GlobalPolicy = TestPolicies.FixedWindowSingle();
                options.OnRejected = async (rejection, cancellationToken) =>
                {
                    rejection.Context.Response.StatusCode = HttpStatusCode.ServiceUnavailable;
                    await rejection.Context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("slow-down"), cancellationToken);
                };
            },
            ctx => Task.CompletedTask);

        // Act
        await using RateLimitTestContext first = new();
        await ExecuteAsync(pipeline, first);

        await using RateLimitTestContext second = new();
        await ExecuteAsync(pipeline, second);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        second.ReadResponseBody().ShouldBe("slow-down");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: The OnDecision hook should observe an admit then a reject")]
    public async Task InvokeAsync_OnDecisionHook_ShouldObserveAdmitThenReject()
    {
        // Arrange
        List<RateLimitingDecision> decisions = new();
        IWebApplicationPipeline pipeline = BuildPipeline(
            options =>
            {
                options.GlobalPolicy = TestPolicies.FixedWindowSingle();
                options.OnDecision = decision => decisions.Add(decision);
            },
            ctx => Task.CompletedTask);

        // Act
        await using RateLimitTestContext first = new();
        await ExecuteAsync(pipeline, first);

        await using RateLimitTestContext second = new();
        await ExecuteAsync(pipeline, second);

        // Assert
        decisions.Count.ShouldBe(2);
        decisions[0].IsAcquired.ShouldBeTrue();
        decisions[1].IsAcquired.ShouldBeFalse();
        decisions[1].RetryAfter.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: The client-address partition key should follow the forwarded effective identity")]
    public async Task InvokeAsync_ClientAddressPartition_ShouldComposeForwardedIdentity()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.GlobalPolicy = TestPolicies.FixedWindowPerClient(),
            ctx =>
            {
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        IPAddress wirePeer = IPAddress.Parse("10.0.0.1");

        // Act
        // A: keyed on the wire peer 10.0.0.1.
        await using RateLimitTestContext directClient = new(remoteIp: wirePeer);
        await ExecuteAsync(pipeline, directClient);

        // B: same wire peer, but a trusted proxy vouches for a different client — its own partition.
        await using RateLimitTestContext proxiedClient = new(remoteIp: wirePeer);
        proxiedClient.Features.Set<IHttpForwardedFeature>(new FakeForwardedFeature(IPAddress.Parse("203.0.113.7")));
        await ExecuteAsync(pipeline, proxiedClient);

        // C: the wire peer 10.0.0.1 again, no forwarding — same partition as A, now exhausted.
        await using RateLimitTestContext directClientAgain = new(remoteIp: wirePeer);
        await ExecuteAsync(pipeline, directClientAgain);

        // Assert
        directClient.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        proxiedClient.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        directClientAgain.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A named per-endpoint policy should gate the matched endpoint")]
    public async Task InvokeAsync_EndpointPolicyByName_ShouldGateMatchedEndpoint()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.AddPolicy("expensive", TestPolicies.FixedWindowSingle("expensive")),
            ctx =>
            {
                ctx.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(new RateLimitingMetadata("expensive")));
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        // Act
        await using RateLimitTestContext first = new();
        await ExecuteAsync(pipeline, first);

        await using RateLimitTestContext second = new();
        await ExecuteAsync(pipeline, second);

        // Assert
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: An inline per-endpoint policy should gate the matched endpoint")]
    public async Task InvokeAsync_EndpointPolicyInline_ShouldGateMatchedEndpoint()
    {
        // Arrange — one metadata instance carries one policy, so both requests hit the same limiter.
        RateLimitingMetadata metadata = new(TestPolicies.FixedWindowSingle("inline"));
        IWebApplicationPipeline pipeline = BuildPipeline(
            configure: null,
            ctx =>
            {
                ctx.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(metadata));
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        // Act
        await using RateLimitTestContext first = new();
        await ExecuteAsync(pipeline, first);

        await using RateLimitTestContext second = new();
        await ExecuteAsync(pipeline, second);

        // Assert
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: Disabled endpoint metadata should bypass the per-endpoint gate")]
    public async Task InvokeAsync_EndpointDisabled_ShouldBypassEndpointGate()
    {
        // Arrange
        RateLimitingPolicy exhausted = TestPolicies.FixedWindowSingle("ep");
        using (exhausted.Limiter.AttemptAcquire(new RateLimitTestContext(), permitCount: 1))
        {
            // The one permit for the window is now consumed.
        }

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.AddPolicy("ep", exhausted),
            ctx =>
            {
                ctx.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(RateLimitingMetadata.Disabled));
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        await using RateLimitTestContext context = new();

        // Act — despite the exhausted policy, Disabled skips the gate, so the handler runs.
        await ExecuteAsync(pipeline, context);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: An unknown policy name should surface an InvalidOperationException")]
    public async Task InvokeAsync_UnknownPolicyName_ShouldThrow()
    {
        // Arrange
        IWebApplicationPipeline pipeline = BuildPipeline(
            configure: null,
            ctx =>
            {
                ctx.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(new RateLimitingMetadata("missing")));
                return Task.CompletedTask;
            });

        await using RateLimitTestContext context = new();

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(ExecuteAsync(pipeline, context));
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A rejection on a started response should abort the exchange instead of writing 429")]
    public async Task InvokeAsync_RejectionOnStartedResponse_ShouldAbortInsteadOfWriting()
    {
        // Arrange
        RateLimitingPolicy exhausted = TestPolicies.FixedWindowSingle("ep");
        using (exhausted.Limiter.AttemptAcquire(new RateLimitTestContext(), permitCount: 1))
        {
        }

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.AddPolicy("ep", exhausted),
            ctx =>
            {
                // The head is already on the wire before the endpoint gate rejects.
                ctx.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(hasStarted: true));
                ctx.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(new RateLimitingMetadata("ep")));
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            });

        await using RateLimitTestContext context = new();

        // Act
        await ExecuteAsync(pipeline, context);

        // Assert — the status cannot be rewritten on a committed head; the exchange is cancelled instead.
        context.CancelRequested.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A concurrency limiter should hold its permit for the request lifetime and reject a concurrent request")]
    public async Task InvokeAsync_ConcurrencyLimiter_ShouldHoldPermitForRequestLifetime()
    {
        // Arrange
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.GlobalPolicy = TestPolicies.ConcurrencySingle(),
            async ctx =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
                ctx.Response.StatusCode = HttpStatusCode.Ok;
            });

        await using RateLimitTestContext first = new();
        await using RateLimitTestContext second = new();

        // Act — request one enters the handler holding the only permit.
        Task firstRequest = ExecuteAsync(pipeline, first);
        await firstEntered.Task.WaitAsync(TestBudget);

        // Request two arrives while the permit is held.
        await ExecuteAsync(pipeline, second);

        releaseFirst.SetResult();
        await firstRequest.WaitAsync(TestBudget);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    private static IWebApplicationPipeline BuildPipeline(
        Action<RateLimitingOptions>? configure,
        WebApplicationMiddleware terminal)
    {
        TestPipelineBuilder builder = new();
        builder.UseRateLimiting(configure);
        builder.Use(next => context => terminal.Invoke(context));

        return builder.Build();
    }

    private static async Task ExecuteAsync(IWebApplicationPipeline pipeline, RateLimitTestContext context)
    {
        using CancellationTokenSource cancellation = new(TestBudget);
        await pipeline.ExecuteAsync(context, cancellation.Token);
    }
}
