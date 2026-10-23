using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResiliencePipeline")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResiliencePipeline : IResiliencePipeline
{
    private readonly ResilienceStrategy _strategy;

    internal ResiliencePipeline(ResilienceStrategy strategy)
    {
        _strategy = strategy;
    }

    async ValueTask IResiliencePipeline.ExecuteAsync(
        ResilienceCallback callback,
        IResilienceContext context,
        object? state)
    {
        Outcome outcome = await _strategy
            .Invoke(callback, context, state)
            .ConfigureAwait(context.ContinueOnCapturedContext);

        // Bubble up exception
        outcome.ThrowIfException();
    }

    private sealed class DebugView
    {
        public DebugView(ResiliencePipeline value)
        {
        }
    }
}