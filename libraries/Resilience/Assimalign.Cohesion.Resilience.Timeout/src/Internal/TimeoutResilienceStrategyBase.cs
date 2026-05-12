using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

using ObjectPool;

internal abstract class TimeoutResilienceStrategyBase
{
    protected TimeoutResilienceStrategyBase(TimeoutStrategyOptions options)
    {
        TimeProvider = options.TimeProvider;
        DefaultTimeout = options.Timeout;
        TimeoutGenerator = options.TimeoutGenerator;
        OnTimeout = options.OnTimeout;
        CancellationTokenSourcePool = new CancellationTokenSourcePool(options.TimeProvider);
    }

    protected TimeProvider TimeProvider { get; }

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
        TimeSpan timeout = DefaultTimeout;

        if (TimeoutGenerator is not null)
        {
            timeout = await TimeoutGenerator
                .Invoke(new TimeoutGeneratorArguments(context))
                .ConfigureAwait(context.ContinueOnCapturedContext);
        }

        if (!TimeoutUtil.ShouldApplyTimeout(timeout))
        {
            return await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        CancellationTokenSource cancellationTokenSource = CancellationTokenSourcePool.Rent(timeout);
        CancellationToken previousCancellationToken = context.CancellationToken;
        CancellationTokenRegistration registration = default;
        bool cleanupDeferred = false;

        try
        {
            registration = previousCancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(),
                cancellationTokenSource);

            IResilienceContext timeoutContext = new TimeoutResilienceContext(context, cancellationTokenSource.Token);
            Task<TOutcome> executionTask = callback.Invoke(timeoutContext, state).AsTask();
            Task timeoutTask = Task.Delay(timeout, TimeProvider, CancellationToken.None);

            Task completedTask = await Task.WhenAny(executionTask, timeoutTask).ConfigureAwait(context.ContinueOnCapturedContext);

            if (ReferenceEquals(completedTask, executionTask))
            {
                TOutcome outcome = await executionTask.ConfigureAwait(context.ContinueOnCapturedContext);

                bool timedOut = cancellationTokenSource.IsCancellationRequested && !previousCancellationToken.IsCancellationRequested;

                if (!timedOut || !IsOutcomeOperationCancelledException(outcome, out OperationCanceledException cancelled))
                {
                    return outcome;
                }

                if (OnTimeout is not null)
                {
                    await OnTimeout.Invoke(new OnTimeoutArguments(context, timeout)).ConfigureAwait(context.ContinueOnCapturedContext);
                }

                return error.Invoke(new TimeoutRejectedException(
                    $"The operation didn't complete within the allowed timeout of '{timeout}'.",
                    timeout,
                    cancelled));
            }

            cancellationTokenSource.Cancel();
            cleanupDeferred = true;
            _ = ObserveLateCompletionAsync(executionTask, registration, cancellationTokenSource);

            if (OnTimeout is not null)
            {
                await OnTimeout.Invoke(new OnTimeoutArguments(context, timeout)).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            return error.Invoke(new TimeoutRejectedException(
                $"The operation didn't complete within the allowed timeout of '{timeout}'.",
                timeout));
        }
        finally
        {
            if (!cleanupDeferred)
            {
                registration.Dispose();
                CancellationTokenSourcePool.Return(cancellationTokenSource);
            }
        }
    }

    private async Task ObserveLateCompletionAsync<TOutcome>(
        Task<TOutcome> executionTask,
        CancellationTokenRegistration registration,
        CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await executionTask.ConfigureAwait(false);
        }
        catch
        {
            return;
        }
        finally
        {
            registration.Dispose();
            CancellationTokenSourcePool.Return(cancellationTokenSource);
        }
    }

    protected abstract bool IsOutcomeOperationCancelledException<TOutcome>(
        TOutcome outcome,
        out OperationCanceledException exception);
}
