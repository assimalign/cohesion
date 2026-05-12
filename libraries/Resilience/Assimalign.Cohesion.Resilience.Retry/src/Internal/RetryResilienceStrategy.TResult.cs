using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class RetryResilienceStrategy<TResult> : RetryResilienceStrategyBase, IResilienceStrategy<TResult>
{
    public RetryResilienceStrategy(RetryStrategyOptions<TResult>  options) 
        : base(
            options.MaxRetryAttempts,
            options.TimeProvider,
            options.Randomizer,
            options.Delay,
            options.MaxDelay,
            options.UseJitter,
            options.BackoffType)
    {
        Retry = options.Retry;
        OnRetry = options.OnRetry;
        DelayGenerator = options.DelayGenerator;
    }

    public Func<RetryPredicateArguments<TResult>, ValueTask<bool>> Retry { get; }
    public Func<RetryDelayGeneratorArguments<TResult>, ValueTask<TimeSpan?>>? DelayGenerator { get; }
    public Func<OnRetryArguments<TResult>, ValueTask>? OnRetry { get; }
    public async ValueTask<Outcome<TResult>> ExecuteAsync(
        ResilienceCallback<TResult> callback, 
        IResilienceContext context,
        object? state)
    {
        double retryState = 0;
        int attempt = 0;

        while (true)
        {
            // Softly check if the pipeline has been cancelled
            if (context.IsPipelineCancelled(out OperationCanceledException? cancelled))
            {
                return cancelled;
            }

            Outcome<TResult> outcome;

            long timestamp = TimeProvider.GetTimestamp();

            try
            {
                outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
            }
            catch (Exception exception)
            {
                outcome = exception;
            }

            // Set Execution Time
            TimeSpan executionTime = TimeProvider.GetElapsedTime(timestamp);

            // Check whether the the callback should be retried 
            bool retry = await Retry
                .Invoke(new RetryPredicateArguments<TResult>(context, outcome, attempt))
                .ConfigureAwait(context.ContinueOnCapturedContext);

            if (IsLastAttempt(attempt, out bool incrementAttempts) || !retry)
            {
                //    TelemetryUtil.ReportFinalExecutionAttempt(_telemetry, context, outcome, attempt, executionTime, handle);
                return outcome;
            }
            else
            {
                //    TelemetryUtil.ReportExecutionAttempt(_telemetry, context, outcome, attempt, executionTime, handle);
            }

            TimeSpan delay = RetryHelper.GetRetryDelay(BackoffType, UseJitter, attempt, Delay, MaxDelay, ref retryState, Randomizer);
            
            if (DelayGenerator is not null)
            {
                TimeSpan? span = await DelayGenerator
                    .Invoke(new RetryDelayGeneratorArguments<TResult>(context, outcome, attempt))
                    .ConfigureAwait(false);

                if (span is TimeSpan newDelay && RetryHelper.IsValidDelay(newDelay))
                {
                    delay = newDelay;
                }
            }

            Debug.Assert(delay >= TimeSpan.Zero, "The delay cannot be negative.");

            OnRetryArguments<TResult> onRetryArgs = new OnRetryArguments<TResult>(context, outcome, attempt, delay, executionTime);
            
            //_telemetry.Report<OnRetryArguments<T>, T>(new(ResilienceEventSeverity.Warning, RetryConstants.OnRetryEvent), onRetryArgs);

            if (OnRetry is not null)
            {
                await OnRetry.Invoke(onRetryArgs).ConfigureAwait(context.ContinueOnCapturedContext);
            }
            //if (outcome.TryGetResult(out var resultValue))
            //{
            //    await DisposeHelper.TryDisposeSafeAsync(resultValue, context.IsSynchronous).ConfigureAwait(context.ContinueOnCapturedContext);
            //}
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await TimeProvider.DelayAsync(delay, context).ConfigureAwait(context.ContinueOnCapturedContext);
                }
            }
            catch (OperationCanceledException exception)
            {
                return exception;
            }

            if (incrementAttempts)
            {
                attempt++;
            }
        }
    }
}
