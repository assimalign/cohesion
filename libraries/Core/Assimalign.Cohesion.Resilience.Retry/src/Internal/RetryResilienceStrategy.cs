using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class RetryResilienceStrategy : RetryResilienceStrategyBase, IResilienceStrategy
{

    public RetryResilienceStrategy(RetryStrategyOptions options)
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

    public Func<RetryPredicateArguments, ValueTask<bool>> Retry { get; }
    public Func<RetryDelayGeneratorArguments, ValueTask<TimeSpan?>>? DelayGenerator { get; }
    public Func<OnRetryArguments, ValueTask>? OnRetry { get; }

    public async ValueTask<Outcome> ExecuteAsync(
        ResilienceCallback callback,
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

            Outcome outcome = Outcome.Success;

            long timestamp = TimeProvider.GetTimestamp();

            try
            {
                await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
            }
            catch (Exception exception)
            {
                outcome = exception;
            }

            TimeSpan executionTime = TimeProvider.GetElapsedTime(timestamp);

            // Check if the callback should be retried
            bool retry = await Retry
                .Invoke(new RetryPredicateArguments(context, outcome, attempt))
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
                RetryDelayGeneratorArguments delayArgs = new RetryDelayGeneratorArguments(context, outcome, attempt);

                if (await DelayGenerator.Invoke(delayArgs).ConfigureAwait(false) is TimeSpan newDelay && RetryHelper.IsValidDelay(newDelay))
                {
                    delay = newDelay;
                }
            }

            Debug.Assert(delay >= TimeSpan.Zero, "The delay cannot be negative.");

            OnRetryArguments onRetryArgs = new OnRetryArguments(context, outcome, attempt, delay, executionTime);
            //_telemetry.Report<OnRetryArguments, T>(new(ResilienceEventSeverity.Warning, RetryConstants.OnRetryEvent), onRetryArgs);

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
