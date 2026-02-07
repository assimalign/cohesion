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
            Outcome<TResult> outcome;

            long timestamp = TimeProvider.GetTimestamp();

            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
            }
            catch (Exception exception)
            {
                outcome = exception;
            }

            // Check whether the 
            bool shouldRetry = await Retry.Invoke(new RetryPredicateArguments<TResult>(context, outcome, attempt))
                .ConfigureAwait(context.ContinueOnCapturedContext);
            
            // Set Execution Time
            TimeSpan executionTime = TimeProvider.GetElapsedTime(timestamp);

            if (IsLastAttempt(attempt, out bool incrementAttempts) || !shouldRetry)
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
                TimeSpan? span = await DelayGenerator.Invoke(new RetryDelayGeneratorArguments<TResult>(context, outcome, attempt))
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
                context.CancellationToken.ThrowIfCancellationRequested();

                // stryker disable once all : no means to test this
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
