using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Resilience;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Resilience.Fallback.Tests;

public class FallbackResilienceStrategyTests
{
    [Fact(DisplayName = "Cohesion Test [Resilience.Fallback] - Strategy: Async fallback action runs for handled failure")]
    public async Task Strategy_ExecuteAsync_ShouldRunFallbackActionOnHandledFailure()
    {
        bool fallbackInvoked = false;

        IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .UseFallback(options =>
            {
                options.FallbackAction = _ =>
                {
                    fallbackInvoked = true;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        await pipeline.ExecuteAsync<object?>(static (_, _) => ValueTask.FromException(new InvalidOperationException("boom")));

        fallbackInvoked.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Resilience.Fallback] - Strategy: Sync execute returns fallback value")]
    public void Strategy_Execute_ShouldReturnFallbackValue()
    {
        IResiliencePipeline<int> pipeline = new ResiliencePipelineBuilder<int>()
            .UseFallback(options =>
            {
                options.FallbackAction = _ => ValueTask.FromResult(42);
            })
            .Build();

        int result = ResilienceExtensions.Execute<int, object?>(pipeline, static (_, _) => throw new InvalidOperationException("boom"));

        result.ShouldBe(42);
    }
}
