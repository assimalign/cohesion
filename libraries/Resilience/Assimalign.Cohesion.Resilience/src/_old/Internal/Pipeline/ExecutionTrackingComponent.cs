using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal.Pipeline;

internal sealed class ExecutionTrackingComponent : PipelineComponent
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan SleepDelay = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private int _pendingExecutions;

    public ExecutionTrackingComponent(PipelineComponent component, TimeProvider timeProvider)
    {
        Component = component;
        _timeProvider = timeProvider;
    }

    public PipelineComponent Component { get; }

    public bool HasPendingExecutions => Volatile.Read(ref _pendingExecutions) > 0;

    internal override async ValueTask<OutcomeO<TResult>> ExecuteAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state)
    {
        Interlocked.Increment(ref _pendingExecutions);

        try
        {
            return await Component.ExecuteAsync(callback, context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingExecutions);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        var start = _timeProvider.GetTimestamp();

        // We don't want to introduce locks or any synchronization primitives to main execution path
        // so we will do "dummy" retries until there are no more executions.
        while (HasPendingExecutions)
        {
            await Task.Delay(SleepDelay, _timeProvider).ConfigureAwait(false);

            // stryker disable once equality : no means to test this
            if (_timeProvider.GetElapsedTime(start) > Timeout)
            {
                break;
            }
        }

        await Component.DisposeAsync().ConfigureAwait(false);
    }
}
