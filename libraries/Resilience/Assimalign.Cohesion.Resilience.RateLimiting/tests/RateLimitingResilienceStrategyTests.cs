using System;
using System.Collections.Generic;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using Assimalign.Cohesion.Resilience;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Resilience.RateLimiting.Tests;

public class RateLimitingResilienceStrategyTests
{
    [Fact(DisplayName = "Cohesion Test [Resilience.RateLimiting] - Strategy: Rejection surfaces the rate limiter exception")]
    public async Task Strategy_ExecuteAsync_ShouldRejectWhenNoPermitIsAvailable()
    {
        using ConcurrencyLimiter limiter = new(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
        });

        using RateLimitLease heldLease = limiter.AttemptAcquire(1);

        bool rejected = false;
        bool executed = false;

        IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .UseRateLimiter(options =>
            {
                options.RateLimiter = limiter;
                options.OnRejected = _ =>
                {
                    rejected = true;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
        {
            await pipeline.ExecuteAsync<object?>((_, _) =>
            {
                executed = true;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        });

        RateLimiterRejectedException rejectedException = ExtractException<RateLimiterRejectedException>(exception);

        executed.ShouldBeFalse();
        rejected.ShouldBeTrue();
        rejectedException.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.RateLimiting] - Strategy: Sync execute succeeds when a permit is available")]
    public void Strategy_Execute_ShouldReturnResultWhenPermitIsAvailable()
    {
        using ConcurrencyLimiter limiter = new(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
        });

        IResiliencePipeline<int> pipeline = new ResiliencePipelineBuilder<int>()
            .UseRateLimiter(options => options.RateLimiter = limiter)
            .Build();

        int result = ResilienceExtensions.Execute<int, object?>(pipeline, static (_, _) => 7);

        result.ShouldBe(7);
    }

    private static TException ExtractException<TException>(Exception exception)
        where TException : Exception
    {
        Queue<Exception> pending = new();
        pending.Enqueue(exception);

        while (pending.Count > 0)
        {
            Exception current = pending.Dequeue();

            if (current is TException match)
            {
                return match;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }

            if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }

        throw new InvalidOperationException($"Unable to locate an exception of type '{typeof(TException).FullName}'.");
    }
}
