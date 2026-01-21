using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class RetryStrategy : ResilienceStrategy
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _randomizer;

    public RetryStrategy(RetryStrategyOptions options)
    {
        ShouldHandle = options.ShouldHandle;
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

    public override async ValueTask ExecuteAsync<TState>(
        ResilienceStrategyCallback<TState> callback, 
        IResilienceContext context, 
        TState state)
    {
        double retryState = 0;
        int attempt = 0;

        while (true)
        {
            long startTimestamp = _timeProvider.GetTimestamp();
            
            Outcome outcome;
            
            try
            {
                outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
            }
            catch (Exception ex)
            {
                outcome = new(ex);
            }

            var shouldRetryArgs = new RetryPredicateArguments(context, outcome, attempt);
            var handle = await ShouldHandle(shouldRetryArgs).ConfigureAwait(context.ContinueOnCapturedContext);
            var executionTime = _timeProvider.GetElapsedTime(startTimestamp);

            var isLastAttempt = IsLastAttempt(attempt, out bool incrementAttempts);
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
                return;
            }

            var delay = RetryHelper.GetRetryDelay(BackoffType, UseJitter, attempt, BaseDelay, MaxDelay, ref retryState, _randomizer);
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

            var onRetryArgs = new OnRetryArguments(context, outcome, attempt, delay, executionTime);
            //_telemetry.Report<OnRetryArguments, T>(new(ResilienceEventSeverity.Warning, RetryConstants.OnRetryEvent), onRetryArgs);

            if (OnRetry is not null)
            {
                await OnRetry(onRetryArgs).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            //if (outcome.TryGetResult(out var resultValue))
            //{
            //    await DisposeHelper.TryDisposeSafeAsync(resultValue, context.IsSynchronous).ConfigureAwait(context.ContinueOnCapturedContext);
            //}

            context.CancellationToken.ThrowIfCancellationRequested();

            //try
            //{
            //    context.CancellationToken.ThrowIfCancellationRequested();

            //    // stryker disable once all : no means to test this
            //    if (delay > TimeSpan.Zero)
            //    {
            //        await _timeProvider.DelayAsync(delay, context).ConfigureAwait(context.ContinueOnCapturedContext);
            //    }

            //}
            //catch (OperationCanceledException e)
            //{
            //    Outcome
            //    return Outcome.FromException<T>(e);
            //}

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
