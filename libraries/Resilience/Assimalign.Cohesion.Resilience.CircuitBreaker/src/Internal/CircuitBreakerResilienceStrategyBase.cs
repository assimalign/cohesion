using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal abstract class CircuitBreakerResilienceStrategyBase
{
    private readonly object _syncRoot = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _failureThreshold;
    private readonly TimeSpan _breakDuration;
    private readonly Func<CircuitBreakerPredicateArguments, ValueTask<bool>> _shouldHandle;
    private readonly Func<OnCircuitOpenedArguments, ValueTask>? _onOpened;
    private readonly Func<OnCircuitHalfOpenedArguments, ValueTask>? _onHalfOpened;
    private readonly Func<OnCircuitClosedArguments, ValueTask>? _onClosed;
    private CircuitBreakerState _state;
    private int _failureCount;
    private DateTimeOffset _openedUntil;

    protected CircuitBreakerResilienceStrategyBase(CircuitBreakerStrategyOptions options)
    {
        if (options.FailureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.FailureThreshold), options.FailureThreshold, "FailureThreshold must be greater than zero.");
        }

        if (options.BreakDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.BreakDuration), options.BreakDuration, "BreakDuration must be greater than zero.");
        }

        _timeProvider = options.TimeProvider;
        _failureThreshold = options.FailureThreshold;
        _breakDuration = options.BreakDuration;
        _shouldHandle = options.ShouldHandle;
        _onOpened = options.OnOpened;
        _onHalfOpened = options.OnHalfOpened;
        _onClosed = options.OnClosed;
        _state = CircuitBreakerState.Closed;
    }

    protected async ValueTask<TOutcome> ExecuteAsync<TOutcome>(
        Func<IResilienceContext, object?, ValueTask<TOutcome>> callback,
        Func<TOutcome, Exception?> getException,
        Func<BrokenCircuitException, TOutcome> rejected,
        IResilienceContext context,
        object? state)
    {
        if (!TryEnter(out bool isProbe, out bool notifyHalfOpen, out BrokenCircuitException? brokenCircuitException))
        {
            return rejected(brokenCircuitException!);
        }

        if (notifyHalfOpen && _onHalfOpened is not null)
        {
            await _onHalfOpened
                .Invoke(new OnCircuitHalfOpenedArguments(context, _breakDuration))
                .ConfigureAwait(context.ContinueOnCapturedContext);
        }

        TOutcome outcome = await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        Exception? exception = getException(outcome);

        if (exception is null)
        {
            await CloseAsync(context, notify: isProbe).ConfigureAwait(context.ContinueOnCapturedContext);
            return outcome;
        }

        int candidateFailureCount = GetCandidateFailureCount(isProbe);
        CircuitBreakerState evaluationState = isProbe ? CircuitBreakerState.HalfOpen : CircuitBreakerState.Closed;

        bool shouldHandle = await _shouldHandle
            .Invoke(new CircuitBreakerPredicateArguments(context, exception, candidateFailureCount, evaluationState))
            .ConfigureAwait(context.ContinueOnCapturedContext);

        if (!shouldHandle)
        {
            if (isProbe)
            {
                await CloseAsync(context, notify: true).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            return outcome;
        }

        await RegisterFailureAsync(context, exception, isProbe).ConfigureAwait(context.ContinueOnCapturedContext);

        return outcome;
    }

    private bool TryEnter(
        out bool isProbe,
        out bool notifyHalfOpen,
        out BrokenCircuitException? brokenCircuitException)
    {
        lock (_syncRoot)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (_state == CircuitBreakerState.Open)
            {
                if (now < _openedUntil)
                {
                    TimeSpan retryAfter = _openedUntil - now;
                    brokenCircuitException = CreateBrokenCircuitException(retryAfter);
                    isProbe = false;
                    notifyHalfOpen = false;
                    return false;
                }

                _state = CircuitBreakerState.HalfOpen;
                brokenCircuitException = null;
                isProbe = true;
                notifyHalfOpen = true;
                return true;
            }

            if (_state == CircuitBreakerState.HalfOpen)
            {
                brokenCircuitException = CreateBrokenCircuitException(TimeSpan.Zero);
                isProbe = false;
                notifyHalfOpen = false;
                return false;
            }

            brokenCircuitException = null;
            isProbe = false;
            notifyHalfOpen = false;
            return true;
        }
    }

    private int GetCandidateFailureCount(bool isProbe)
    {
        lock (_syncRoot)
        {
            return isProbe ? _failureThreshold : _failureCount + 1;
        }
    }

    private async ValueTask RegisterFailureAsync(
        IResilienceContext context,
        Exception exception,
        bool isProbe)
    {
        bool opened;
        int failureCount;

        lock (_syncRoot)
        {
            if (isProbe)
            {
                _failureCount = _failureThreshold;
            }
            else
            {
                _failureCount++;
            }

            failureCount = _failureCount;
            opened = isProbe || _failureCount >= _failureThreshold;

            if (opened)
            {
                _state = CircuitBreakerState.Open;
                _openedUntil = _timeProvider.GetUtcNow().Add(_breakDuration);
            }
        }

        if (opened && _onOpened is not null)
        {
            await _onOpened
                .Invoke(new OnCircuitOpenedArguments(context, exception, failureCount, _breakDuration))
                .ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }

    private async ValueTask CloseAsync(IResilienceContext context, bool notify)
    {
        lock (_syncRoot)
        {
            _state = CircuitBreakerState.Closed;
            _failureCount = 0;
            _openedUntil = default;
        }

        if (notify && _onClosed is not null)
        {
            await _onClosed
                .Invoke(new OnCircuitClosedArguments(context))
                .ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }

    private static BrokenCircuitException CreateBrokenCircuitException(TimeSpan retryAfter)
    {
        string message = retryAfter > TimeSpan.Zero
            ? $"The circuit breaker is open for another '{retryAfter}'."
            : "The circuit breaker is half-open and already probing a recovery execution.";

        return new BrokenCircuitException(message, retryAfter);
    }
}
