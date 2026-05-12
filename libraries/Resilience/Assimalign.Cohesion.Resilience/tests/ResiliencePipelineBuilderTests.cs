using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Resilience.Tests;

public class ResiliencePipelineBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Resilience] - Builder: Execute invokes the callback through the strategy")]
    public void Builder_Execute_ShouldInvokeCallback()
    {
        IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .UseStrategy(static async (callback, context, state) =>
            {
                await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
                return Outcome.Success;
            })
            .Build();

        bool executed = false;

        pipeline.Execute<object?>((_, _) =>
        {
            executed = true;
        });

        executed.ShouldBeTrue();
    }
}
