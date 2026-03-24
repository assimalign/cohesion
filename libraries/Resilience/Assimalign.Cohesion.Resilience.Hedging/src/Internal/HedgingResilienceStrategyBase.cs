using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal abstract class HedgingResilienceStrategyBase
{
    private readonly TimeProvider _timeProvider;
    private readonly int _maxHedgedAttempts;
    private readonly TimeSpan _delay;
    private readonly Func<HedgingPredicateArguments, ValueTask<bool>> _shouldHandle;
    private readonly Func<OnHedgingArguments, ValueTask>? _onHedging;

    protected HedgingResilienceStrategyBase(HedgingStrategyOptions options)
    {
        if (options.MaxHedgedAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxHedgedAttempts), options.MaxHedgedAttempts, "MaxHedgedAttempts must be greater than zero.");
        }

        if (options.Delay < TimeSpan.Zero && options.Delay != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Delay), options.Delay, "Delay must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        _timeProvider = options.TimeProvider;
        _maxHedgedAttempts = options.MaxHedgedAttempts;
        _delay = options.Delay;
        _shouldHandle = options.ShouldHandle;
        _onHedging = options.OnHedging;
    }

    protected async ValueTask<TOutcome> ExecuteAsync<TOutcome>(
        Func<IResilienceContext, object?, ValueTask<TOutcome>> callback,
        Func<TOutcome, Exception?> getException,
        Func<Exception, TOutcome> error,
        IResilienceContext context,
        object? state)
    {
        using CancellationTokenSource hedgeCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        List<Task<TOutcome>> attempts = [StartAttempt(callback, context, state, hedgeCancellation.Token)];
        List<Exception> failures = [];
        int nextAttempt = 1;
        Task? nextHedgeDelay = CreateDelayTask(hedgeCancellation.Token, nextAttempt);

        while (attempts.Count > 0 || nextHedgeDelay is not null)
        {
            List<Task> pending = new(attempts.Count + (nextHedgeDelay is null ? 0 : 1));

            foreach (Task<TOutcome> attempt in attempts)
            {
                pending.Add(attempt);
            }

            if (nextHedgeDelay is not null)
            {
                pending.Add(nextHedgeDelay);
            }

            Task completed = await Task.WhenAny(pending).ConfigureAwait(context.ContinueOnCapturedContext);

            if (nextHedgeDelay is not null && ReferenceEquals(completed, nextHedgeDelay))
            {
                if (!hedgeCancellation.IsCancellationRequested && nextAttempt < _maxHedgedAttempts)
                {
                    if (_onHedging is not null)
                    {
                        await _onHedging
                            .Invoke(new OnHedgingArguments(context, nextAttempt, _delay))
                            .ConfigureAwait(context.ContinueOnCapturedContext);
                    }

                    attempts.Add(StartAttempt(callback, context, state, hedgeCancellation.Token));
                    nextAttempt++;
                }

                nextHedgeDelay = CreateDelayTask(hedgeCancellation.Token, nextAttempt);
                continue;
            }

            Task<TOutcome> completedAttempt = (Task<TOutcome>)completed;
            attempts.Remove(completedAttempt);

            TOutcome outcome = await completedAttempt.ConfigureAwait(context.ContinueOnCapturedContext);
            Exception? exception = getException(outcome);

            if (exception is null)
            {
                hedgeCancellation.Cancel();
                return outcome;
            }

            bool shouldHandle = await _shouldHandle
                .Invoke(new HedgingPredicateArguments(context, exception, failures.Count))
                .ConfigureAwait(context.ContinueOnCapturedContext);

            if (!shouldHandle)
            {
                hedgeCancellation.Cancel();
                return outcome;
            }

            failures.Add(exception);
        }

        Exception failure = failures.Count switch
        {
            0 => new InvalidOperationException("The hedging strategy completed without producing an outcome."),
            1 => failures[0],
            _ => new AggregateException(failures),
        };

        return error(failure);
    }

    private Task? CreateDelayTask(CancellationToken cancellationToken, int nextAttempt)
    {
        if (nextAttempt >= _maxHedgedAttempts || _delay == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        return Task.Delay(_delay, _timeProvider, cancellationToken);
    }

    private Task<TOutcome> StartAttempt<TOutcome>(
        Func<IResilienceContext, object?, ValueTask<TOutcome>> callback,
        IResilienceContext context,
        object? state,
        CancellationToken cancellationToken)
    {
        return RunAttemptAsync(callback, context, state, cancellationToken).AsTask();
    }

    private async ValueTask<TOutcome> RunAttemptAsync<TOutcome>(
        Func<IResilienceContext, object?, ValueTask<TOutcome>> callback,
        IResilienceContext context,
        object? state,
        CancellationToken cancellationToken)
    {
        IResilienceContext attemptContext = new HedgingResilienceContext(context, cancellationToken);

        return await callback.Invoke(attemptContext, state).ConfigureAwait(context.ContinueOnCapturedContext);
    }
}
