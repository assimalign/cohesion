using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Retry.Tests;

public class RetryResilienceStrategyTest
{
    [Fact]
    public async Task ExecuteAsync_CanceledBeforeExecution_EnsureNotExecuted()
    {
        var pipeline = CreatePipeline();
        var executed = false;

        var context = new TestContext()
        {
            CancellationToken = new CancellationToken(canceled: true)
        };

        var result = await pipeline.ExecuteAsync(async (context, state) =>
        {
            executed = true;
            return new object();
        },
        context,
        default(object));

       // result.Exception.ShouldBeAssignableTo<OperationCanceledException>();
        executed.ShouldBeFalse();
    }



    class TestContext : IResilienceContext
    {
        public OperationKey OperationKey { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public bool ContinueOnCapturedContext { get; set; }
    }
    private IResiliencePipeline<object> CreatePipeline()
    {
        return new ResiliencePipelineBuilder<object>()
            .UseRetry(options =>
            {

            })
            .Build();
    }
}
