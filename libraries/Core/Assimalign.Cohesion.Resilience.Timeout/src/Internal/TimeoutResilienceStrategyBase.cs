using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal abstract class TimeoutResilienceStrategyBase
{
    protected TimeoutResilienceStrategyBase(TimeoutStrategyOptions options)
    {
        DefaultTimeout = options.Timeout;
        TimeoutGenerator = options.TimeoutGenerator;
        OnTimeout = options.OnTimeout;
        CancellationTokenSourcePool = new CancellationTokenSourcePool(options.TimeProvider);
    }

    protected TimeSpan DefaultTimeout { get; }
    protected Func<TimeoutGeneratorArguments, ValueTask<TimeSpan>>? TimeoutGenerator { get; }
    protected Func<OnTimeoutArguments, ValueTask>? OnTimeout { get; }
    protected CancellationTokenSourcePool CancellationTokenSourcePool { get; }
    protected async ValueTask<TOutcome> InvokeAsync<TOutcome>(
        Func<IResilienceContext, object?, ValueTask<TOutcome>> callback,
        Func<TimeoutRejectedException, TOutcome> error,
        IResilienceContext context,
        object? state)
    {
        TOutcome outcome;

        TimeSpan timeout = DefaultTimeout;

        if (TimeoutGenerator is not null)
        {
            timeout = await TimeoutGenerator!.Invoke(new TimeoutGeneratorArguments(context)).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        if (!TimeoutUtil.ShouldApplyTimeout(timeout))
        {
            // do nothing
            outcome = await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        else
        {
            CancellationToken previousCancellationToken = context.CancellationToken;
            CancellationTokenSource cancellationTokenSource = CancellationTokenSourcePool.Rent(timeout);
            TimeoutResilienceContext wrapper = new TimeoutResilienceContext(
                cancellationTokenSource.Token,
                context.ContinueOnCapturedContext,
                context.OperationKey);

            CancellationTokenRegistration cancellationTokenRegistration =
                previousCancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), cancellationTokenSource);

            outcome = await callback.Invoke(wrapper, state);

            // Set flags
            bool isPreviousCancellationRequested = previousCancellationToken.IsCancellationRequested;
            bool isCancellationRequested = cancellationTokenSource.IsCancellationRequested;

            cancellationTokenRegistration.Dispose();

            CancellationTokenSourcePool.Return(cancellationTokenSource);

            if (isCancellationRequested && IsOutcomeOperationCancelledException<TOutcome>(outcome, out OperationCanceledException exception) && !isPreviousCancellationRequested)
            {
                OnTimeoutArguments args = new OnTimeoutArguments(context, timeout);

                //_telemetry.Report(new(ResilienceEventSeverity.Error, TimeoutConstants.OnTimeoutEvent), context, args);

                if (OnTimeout is not null)
                {
                    await OnTimeout.Invoke(args).ConfigureAwait(context.ContinueOnCapturedContext);
                }

                outcome = error.Invoke(new TimeoutRejectedException(
                    $"The operation didn't complete within the allowed timeout of '{timeout}'.",
                    timeout,
                    exception));
            }
        }

        return outcome;
    }

    protected abstract bool IsOutcomeOperationCancelledException<TOutcome>(TOutcome outcome, out OperationCanceledException exception);
}
