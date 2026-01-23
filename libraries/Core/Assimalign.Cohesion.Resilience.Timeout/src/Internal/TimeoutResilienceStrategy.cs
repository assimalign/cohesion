using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class TimeoutResilienceStrategy : TimeoutResilienceStrategyBase, IResilienceStrategy
{
    public TimeoutResilienceStrategy(TimeoutStrategyOptions options) 
        : base(options) { }

    public async ValueTask ExecuteAsync<TState>(ResilienceStrategyCallback<TState> callback, IResilienceContext context, TState state)
    {
        TimeSpan timeout = DefaultTimeout;

        if (TimeoutGenerator is not null)
        {
            timeout = await TimeoutGenerator!.Invoke(new TimeoutGeneratorArguments(context)).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        if (!TimeoutUtil.ShouldApplyTimeout(timeout))
        {
            // do nothing
            await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        else
        {
            CancellationToken previousCancellationToken = context.CancellationToken;
            CancellationTokenSource cancellationTokenSource = CancellationTokenSourcePool.Rent(timeout);
            TimeoutResilienceContext wrapper = new TimeoutResilienceContext(
                cancellationTokenSource.Token,
                context.ContinueOnCapturedContext,
                context.OperationKey);

            CancellationTokenRegistration cancellationTokenRegistration = CreateRegistration(cancellationTokenSource, previousCancellationToken);
            Outcome outcome;
            try
            {
                outcome = await callback.Invoke(wrapper, state).ConfigureAwait(wrapper.ContinueOnCapturedContext);
            }
            catch (Exception ex)
            {
                outcome = new Outcome(ex);
            }

            bool isPreviousCancellationRequested = previousCancellationToken.IsCancellationRequested;
            bool isCancellationRequested = cancellationTokenSource.IsCancellationRequested;

            cancellationTokenRegistration.Dispose();
            
            CancellationTokenSourcePool.Return(cancellationTokenSource);

            if (isCancellationRequested && !outcome.IsSuccess && ((Exception)outcome) is OperationCanceledException exception && !isPreviousCancellationRequested)
            {
                OnTimeoutArguments args = new OnTimeoutArguments(context, timeout);

                //_telemetry.Report(new(ResilienceEventSeverity.Error, TimeoutConstants.OnTimeoutEvent), context, args);

                if (OnTimeout is not null)
                {
                    await OnTimeout.Invoke(args).ConfigureAwait(context.ContinueOnCapturedContext);
                }

                throw new TimeoutRejectedException(
                    $"The operation didn't complete within the allowed timeout of '{timeout}'.",
                    timeout,
                    exception);
            }
        }
    }
}
