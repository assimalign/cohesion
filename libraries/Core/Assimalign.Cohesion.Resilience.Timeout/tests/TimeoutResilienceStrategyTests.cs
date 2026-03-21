using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Resilience;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Resilience.Timeout.Tests;

public class TimeoutResilienceStrategyTests
{
    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Strategy: Timeout rejects slow execution")]
    public async Task Strategy_ExecuteAsync_ShouldRejectWhenTimeoutExpires()
    {
        IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .UseTimeout(options => options.Timeout = TimeSpan.FromMilliseconds(20))
            .Build();

        ResilienceException exception = await Should.ThrowAsync<ResilienceException>(async () =>
        {
            await pipeline.ExecuteAsync<object?>(static async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }).ConfigureAwait(false);
        });

        TimeoutRejectedException timeoutException = ExtractException<TimeoutRejectedException>(exception);

        timeoutException.Timeout.ShouldBe(TimeSpan.FromMilliseconds(20));
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Timeout] - Builder: Retry and timeout remain chainable through interfaces")]
    public async Task Builder_UseRetryThenUseTimeout_ShouldRemainChainable()
    {
        IResiliencePipelineBuilder<int> builder = new ResiliencePipelineBuilder<int>();

        IResiliencePipeline<int> pipeline = builder
            .UseRetry(_ => { })
            .UseTimeout(options => options.Timeout = TimeSpan.FromSeconds(1))
            .Build();

        int result = await ResilienceExtensions.ExecuteAsync<int, object?>(pipeline, static (_, _) => ValueTask.FromResult(5));

        result.ShouldBe(5);
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
