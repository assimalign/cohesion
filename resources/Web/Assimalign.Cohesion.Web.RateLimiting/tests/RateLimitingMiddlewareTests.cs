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
/// Middleware-level coverage for the middleware <see cref="RateLimitingBuilder"/> builds, over the
/// pipeline harness: the global gate (admit / 429 / Retry-After / OnRejected / OnDecision / custom
/// status), the endpoint gate over the endpoint published ahead of the middleware (named / inline /
/// disabled / unknown policy, queueing and cancellation, CORS-preflight skip), forwarded-composing
/// client-address partitioning, the started-response abort, and the request-lifetime permit hold.
/// The <c>UseRateLimiting</c> tests cover placement: the registered middleware, the last of two
/// registrations, and the defaults without one.
/// </summary>
public class RateLimitingMiddlewareTests
{
    private static readonly TimeSpan _testBudget = TimeSpan.FromSeconds(30);

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

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A named per-endpoint policy should gate the published endpoint and not run it on rejection")]
    public async Task InvokeAsync_EndpointPolicyByName_ShouldGateMatchedEndpoint()
    {
        // Arrange
        int endpointInvocations = 0;
        IWebApplicationPipeline pipeline = BuildPipeline(
            options => options.AddPolicy("expensive", TestPolicies.FixedWindowSingle("expensive")),
            ctx =>
            {
                endpointInvocations++;
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            },
            endpoint: new FakeRouteMatchFeature(new RateLimitingMetadata("expensive")));

        // Act
        await using RateLimitTestContext first = new();
        await ExecuteAsync(pipeline, first);

        await using RateLimitTestContext second = new();
        await ExecuteAsync(pipeline, second);

        // Assert
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        endpointInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: An inline per-endpoint policy should gate the published endpoint")]
    public async Task InvokeAsync_EndpointPolicyInline_ShouldGateMatchedEndpoint()
    {
        // Arrange — one metadata instance carries one policy, so both requests hit the same limiter.
        RateLimitingMetadata metadata = new(TestPolicies.FixedWindowSingle("inline"));
        IWebApplicationPipeline pipeline = BuildPipeline(
            configure: null,
            ctx =>
            {
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            },
            endpoint: new FakeRouteMatchFeature(metadata));

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
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            },
            endpoint: new FakeRouteMatchFeature(RateLimitingMetadata.Disabled));

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
            ctx => Task.CompletedTask,
            endpoint: new FakeRouteMatchFeature(new RateLimitingMetadata("missing")));

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
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            },
            endpoint: new FakeRouteMatchFeature(new RateLimitingMetadata("ep")));

        await using RateLimitTestContext context = new();

        // A middleware ahead of rate limiting already put the head on the wire before the endpoint gate rejects.
        context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature(hasStarted: true));

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
        await firstEntered.Task.WaitAsync(_testBudget);

        // Request two arrives while the permit is held.
        await ExecuteAsync(pipeline, second);

        releaseFirst.SetResult();
        await firstRequest.WaitAsync(_testBudget);

        // Assert
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A queueing endpoint policy should hold a concurrent request and admit it once the permit is released")]
    public async Task InvokeAsync_QueueingEndpointPolicy_ShouldAdmitQueuedRequestAfterRelease()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testBudget);
        CancellationToken cancellationToken = cancellation.Token;

        RateLimitingPolicy policy = TestPolicies.ConcurrencyQueued("queued");
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int endpointInvocations = 0;

        IWebApplicationPipeline pipeline = BuildPipeline(
            configure: null,
            async ctx =>
            {
                if (Interlocked.Increment(ref endpointInvocations) == 1)
                {
                    firstEntered.SetResult();
                    await releaseFirst.Task;
                }

                ctx.Response.StatusCode = HttpStatusCode.Ok;
            },
            endpoint: new FakeRouteMatchFeature(new RateLimitingMetadata(policy)));

        await using RateLimitTestContext first = new();
        await using RateLimitTestContext second = new();

        // Act — request one reaches the endpoint holding the only permit.
        Task firstRequest = ExecuteAsync(pipeline, first);
        await firstEntered.Task.WaitAsync(cancellationToken);

        // Request two queues for the permit instead of being rejected.
        Task secondRequest = ExecuteAsync(pipeline, second);
        await WaitForQueuedRequestAsync(policy, second, cancellationToken);
        bool secondCompletedWhileQueued = secondRequest.IsCompleted;
        int invocationsWhileQueued = Volatile.Read(ref endpointInvocations);

        // Request one completes, which releases its permit to request two.
        releaseFirst.SetResult();
        await Task.WhenAll(firstRequest, secondRequest).WaitAsync(cancellationToken);

        // Assert
        secondCompletedWhileQueued.ShouldBeFalse();
        invocationsWhileQueued.ShouldBe(1);
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        endpointInvocations.ShouldBe(2);
        policy.Limiter.GetStatistics(first)!.CurrentAvailablePermits.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A request cancelled while queued for an endpoint policy should stop waiting and never run the endpoint")]
    public async Task InvokeAsync_QueuedRequestCancelled_ShouldStopWaitingAndSkipEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testBudget);
        CancellationToken cancellationToken = cancellation.Token;

        RateLimitingPolicy policy = TestPolicies.ConcurrencyQueued("queued");
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int endpointInvocations = 0;

        IWebApplicationPipeline pipeline = BuildPipeline(
            configure: null,
            async ctx =>
            {
                Interlocked.Increment(ref endpointInvocations);
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                ctx.Response.StatusCode = HttpStatusCode.Ok;
            },
            endpoint: new FakeRouteMatchFeature(new RateLimitingMetadata(policy)));

        await using RateLimitTestContext first = new();
        await using RateLimitTestContext second = new();

        Task firstRequest = ExecuteAsync(pipeline, first);
        await firstEntered.Task.WaitAsync(cancellationToken);

        Task secondRequest = ExecuteAsync(pipeline, second);
        await WaitForQueuedRequestAsync(policy, second, cancellationToken);

        // Act — the client of request two goes away while it waits in the limiter's queue.
        second.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(secondRequest.WaitAsync(cancellationToken));

        releaseFirst.SetResult();
        await firstRequest.WaitAsync(cancellationToken);

        endpointInvocations.ShouldBe(1);
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        policy.Limiter.GetStatistics(first)!.CurrentAvailablePermits.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: A CORS preflight should skip its candidate endpoint's policy")]
    public async Task InvokeAsync_PreflightCandidate_ShouldSkipEndpointPolicy()
    {
        // Arrange — the candidate's policy is exhausted, so applying it would reject the preflight.
        RateLimitingPolicy exhausted = TestPolicies.FixedWindowSingle("ep");
        using (exhausted.Limiter.AttemptAcquire(new RateLimitTestContext(), permitCount: 1))
        {
        }

        List<RateLimitingDecision> decisions = new();
        bool continued = false;

        IWebApplicationPipeline pipeline = BuildPipeline(
            options =>
            {
                options.AddPolicy("ep", exhausted);
                options.OnDecision = decisions.Add;
            },
            ctx =>
            {
                continued = true;
                return Task.CompletedTask;
            },
            endpoint: new FakeRouteMatchFeature(new RateLimitingMetadata("ep")) { IsPreflight = true });

        await using RateLimitTestContext context = new();

        // Act
        await ExecuteAsync(pipeline, context);

        // Assert — the preflight continues down the pipeline untouched by the endpoint's limiter.
        continued.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        decisions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - InvokeAsync: The global limiter and the endpoint policy should both decide, global first")]
    public async Task InvokeAsync_GlobalAndEndpointPolicies_ShouldDecideGlobalThenEndpoint()
    {
        // Arrange
        List<RateLimitingDecision> decisions = new();
        string? policyNameSeenByEndpoint = null;

        IWebApplicationPipeline pipeline = BuildPipeline(
            options =>
            {
                options.GlobalPolicy = TestPolicies.FixedWindowSingle("global");
                options.AddPolicy("ep", TestPolicies.FixedWindowSingle("ep"));
                options.OnDecision = decisions.Add;
            },
            ctx =>
            {
                policyNameSeenByEndpoint = ctx.Features.Get<IRateLimitingFeature>()?.PolicyName;
                ctx.Response.StatusCode = HttpStatusCode.Ok;
                return Task.CompletedTask;
            },
            endpoint: new FakeRouteMatchFeature(new RateLimitingMetadata("ep")));

        await using RateLimitTestContext context = new();

        // Act
        await ExecuteAsync(pipeline, context);

        // Assert
        decisions.Count.ShouldBe(2);
        decisions[0].PolicyName.ShouldBeNull();
        decisions[0].IsAcquired.ShouldBeTrue();
        decisions[1].PolicyName.ShouldBe("ep");
        decisions[1].IsAcquired.ShouldBeTrue();
        policyNameSeenByEndpoint.ShouldBe("ep");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - UseRateLimiting: Should place the middleware the application registered")]
    public async Task UseRateLimiting_WithRegisteredMiddleware_ShouldPlaceIt()
    {
        // Arrange — the registration's global policy admits one request per window.
        TestPipelineBuilder builder = new();
        builder.Registered.Add(BuildRateLimiting(options => options.GlobalPolicy = TestPolicies.FixedWindowSingle()));
        builder.UseRateLimiting();
        builder.Use(next => context =>
        {
            context.Response.StatusCode = HttpStatusCode.Ok;
            return Task.CompletedTask;
        });
        IWebApplicationPipeline pipeline = builder.Build();

        await using RateLimitTestContext first = new();
        await using RateLimitTestContext second = new();

        // Act
        await ExecuteAsync(pipeline, first);
        await ExecuteAsync(pipeline, second);

        // Assert
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - UseRateLimiting: Should place the last registration when rate limiting is registered twice")]
    public async Task UseRateLimiting_WithTwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange — both registrations exhaust after one request; only their rejection status differs.
        TestPipelineBuilder builder = new();
        builder.Registered.Add(BuildRateLimiting(options =>
        {
            options.GlobalPolicy = TestPolicies.FixedWindowSingle("first");
            options.RejectionStatusCode = HttpStatusCode.ServiceUnavailable;
        }));
        builder.Registered.Add(BuildRateLimiting(options => options.GlobalPolicy = TestPolicies.FixedWindowSingle("last")));
        builder.UseRateLimiting();
        builder.Use(next => context =>
        {
            context.Response.StatusCode = HttpStatusCode.Ok;
            return Task.CompletedTask;
        });
        IWebApplicationPipeline pipeline = builder.Build();

        await using RateLimitTestContext first = new();
        await using RateLimitTestContext second = new();

        // Act
        await ExecuteAsync(pipeline, first);
        await ExecuteAsync(pipeline, second);

        // Assert — the last registration's default 429, not the first registration's 503.
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - UseRateLimiting: Should place the defaults when the application registered none")]
    public async Task UseRateLimiting_WithoutRegistration_ShouldPlaceTheDefaults()
    {
        // Arrange — the defaults limit nothing globally, but the middleware still runs: it publishes the
        // feature and applies an endpoint's inline policy.
        TestPipelineBuilder builder = new();
        FakeRouteMatchFeature endpoint = new(new RateLimitingMetadata(TestPolicies.FixedWindowSingle("inline")));
        int endpointInvocations = 0;
        bool featurePresent = false;

        builder.Use(next => context =>
        {
            context.Features.Set<IRouteMatchFeature>(endpoint);
            return next.Invoke(context);
        });
        builder.UseRateLimiting();
        builder.Use(next => context =>
        {
            endpointInvocations++;
            featurePresent = context.Features.Get<IRateLimitingFeature>() is not null;
            context.Response.StatusCode = HttpStatusCode.Ok;
            return Task.CompletedTask;
        });
        IWebApplicationPipeline pipeline = builder.Build();

        await using RateLimitTestContext first = new();
        await using RateLimitTestContext second = new();

        // Act
        await ExecuteAsync(pipeline, first);
        await ExecuteAsync(pipeline, second);

        // Assert
        first.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        featurePresent.ShouldBeTrue();
        second.Response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        endpointInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - Build: Options changed after Build should not reach the built middleware")]
    public async Task Build_PolicyAddedAfterBuild_ShouldNotReachTheMiddleware()
    {
        // Arrange — Build snapshots the options; the endpoint names a policy added only afterwards.
        RateLimitingBuilder rateLimiting = new();
        IWebApplicationMiddleware middleware = rateLimiting.Build();
        rateLimiting.Options.AddPolicy("late", TestPolicies.FixedWindowSingle("late"));
        rateLimiting.Options.GlobalPolicy = TestPolicies.FixedWindowSingle("late-global");

        TestPipelineBuilder builder = new();
        builder.Use(next => context =>
        {
            context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(new RateLimitingMetadata("late")));
            return next.Invoke(context);
        });
        builder.Use(middleware);
        builder.Use(next => context => Task.CompletedTask);
        IWebApplicationPipeline pipeline = builder.Build();

        await using RateLimitTestContext context = new();

        // Act / Assert — the snapshot names no policy, so resolving "late" fails.
        await Should.ThrowAsync<InvalidOperationException>(() => ExecuteAsync(pipeline, context));
    }

    private static IWebApplicationMiddleware BuildRateLimiting(Action<RateLimitingOptions>? configure)
    {
        RateLimitingBuilder rateLimiting = new();
        configure?.Invoke(rateLimiting.Options);

        return rateLimiting.Build();
    }

    private static IWebApplicationPipeline BuildPipeline(
        Action<RateLimitingOptions>? configure,
        WebApplicationMiddleware terminal,
        IRouteMatchFeature? endpoint = null)
    {
        TestPipelineBuilder builder = new();

        if (endpoint is not null)
        {
            // What UseRouting does ahead of the middleware: publish the request's endpoint, then call next.
            builder.Use(next => context =>
            {
                context.Features.Set<IRouteMatchFeature>(endpoint);
                return next.Invoke(context);
            });
        }

        builder.Use(BuildRateLimiting(configure));
        builder.Use(next => context => terminal.Invoke(context));

        return builder.Build();
    }

    private static async Task ExecuteAsync(IWebApplicationPipeline pipeline, RateLimitTestContext context)
    {
        using CancellationTokenSource cancellation = new(_testBudget);
        await pipeline.ExecuteAsync(context, cancellation.Token);
    }

    // Waits until the request is parked in the policy's queue, so a test can act while it is queued.
    private static async Task WaitForQueuedRequestAsync(RateLimitingPolicy policy, RateLimitTestContext context, CancellationToken cancellationToken)
    {
        while (policy.Limiter.GetStatistics(context)?.CurrentQueuedCount is not > 0)
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
