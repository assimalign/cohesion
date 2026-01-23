using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class RetryResilienceStrategy : IResilienceStrategy
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _randomizer;

    public RetryResilienceStrategy(RetryStrategyOptions options)
    {
        ShouldHandle = options.ShouldRetry;
        BaseDelay = options.Delay;
        MaxDelay = options.MaxDelay;
        BackoffType = options.BackoffType;
        RetryCount = options.MaxRetryAttempts;
        OnRetry = options.OnRetry;
        DelayGenerator = options.DelayGenerator;
        UseJitter = options.UseJitter;

        _timeProvider = options.TimeProvider;
        _randomizer = options.Randomizer;
    }

    public TimeSpan BaseDelay { get; }
    public TimeSpan? MaxDelay { get; }
    public int RetryCount { get; }
    public bool UseJitter { get; }
    public DelayBackoffType BackoffType { get; }
    public Func<RetryPredicateArguments, ValueTask<bool>> ShouldHandle { get; }
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
            long startTimestamp = _timeProvider.GetTimestamp();
            Outcome outcome;

            try
            {
                await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);

                outcome = true;
            }
            catch (Exception ex)
            {
                outcome = new Outcome(ex);
            }

            RetryPredicateArguments shouldRetryArgs = new RetryPredicateArguments(context, outcome, attempt);
            bool handle = await ShouldHandle.Invoke(shouldRetryArgs).ConfigureAwait(context.ContinueOnCapturedContext);
            TimeSpan executionTime = _timeProvider.GetElapsedTime(startTimestamp);

            bool isLastAttempt = IsLastAttempt(attempt, out bool incrementAttempts);
            //if (isLastAttempt)
            //{
            //    TelemetryUtil.ReportFinalExecutionAttempt(_telemetry, context, outcome, attempt, executionTime, handle);
            //}
            //else
            //{
            //    TelemetryUtil.ReportExecutionAttempt(_telemetry, context, outcome, attempt, executionTime, handle);
            //}

            if (isLastAttempt || !handle)
            {
                return outcome;
            }

            TimeSpan delay = RetryHelper.GetRetryDelay(BackoffType, UseJitter, attempt, BaseDelay, MaxDelay, ref retryState, _randomizer);
            if (DelayGenerator is not null)
            {
                var delayArgs = new RetryDelayGeneratorArguments(context, outcome, attempt);

                if (await DelayGenerator(delayArgs).ConfigureAwait(false) is TimeSpan newDelay && RetryHelper.IsValidDelay(newDelay))
                {
                    delay = newDelay;
                }
            }

#pragma warning disable S3236 // Remove this argument from the method call; it hides the caller information.
            Debug.Assert(delay >= TimeSpan.Zero, "The delay cannot be negative.");
#pragma warning restore S3236 // Remove this argument from the method call; it hides the caller information.

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
                context.CancellationToken.ThrowIfCancellationRequested();

                // stryker disable once all : no means to test this
                if (delay > TimeSpan.Zero)
                {
                    //await _timeProvider.DelayAsync(delay, context).ConfigureAwait(context.ContinueOnCapturedContext);
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

    internal bool IsLastAttempt(int attempt, out bool incrementAttempts)
    {
        if (attempt == int.MaxValue)
        {
            incrementAttempts = false;
            return false;
        }

        incrementAttempts = true;
        return attempt >= RetryCount;
    }
}
