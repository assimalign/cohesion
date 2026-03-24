using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

#pragma warning disable CA1031 // Do not catch general exception types
#pragma warning disable RS0027 // API with optional parameter(s) should have the most parameters amongst its public overloads

public partial class ResiliencePipelineO
{
    /// <summary>
    /// Executes the specified outcome-based callback.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the callback.</typeparam>
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para><strong>Important:</strong> This API targets advanced, low-allocation scenarios. The user callback
    /// must not throw an exception. Wrap your code and return <see cref="OutcomeO{TResult}"/>:
    /// use <see cref="OutcomeO.FromResult{TResult}(TResult)"/> on success, or <see cref="OutcomeO.FromException{TResult}(Exception)"/> on failure.
    /// Do not rely on strategies to catch your exceptions; any such behavior is an implementation detail and is not
    /// guaranteed across strategies or future versions.</para>
    /// </remarks>
    public ValueTask<OutcomeO<TResult>> ExecuteOutcomeAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        InitializeAsyncContext<TResult>(context);

        return Component.ExecuteAsync(callback, context, state);
    }

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the callback.</typeparam>
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public async ValueTask<TResult> ExecuteAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<TResult>> callback,
        ResilienceContextO context,
        TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        InitializeAsyncContext<TResult>(context);

        var outcome = await Component.ExecuteAsync(
            [DebuggerDisableUserUnhandledExceptions] static async (context, state) =>
            {
                try
                {
                    return OutcomeO.FromResult(await state.callback(context, state.state).ConfigureAwait(context.ContinueOnCapturedContext));
                }
                catch (Exception e)
                {
                    return OutcomeO.FromException<TResult>(e);
                }
            },
            context,
            (callback, state)).ConfigureAwait(context.ContinueOnCapturedContext);

        return outcome.GetResultOrRethrow();
    }

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public async ValueTask<TResult> ExecuteAsync<TResult>(
        Func<ResilienceContextO, ValueTask<TResult>> callback,
        ResilienceContextO context)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        InitializeAsyncContext<TResult>(context);

        var outcome = await Component.ExecuteAsync(
            [DebuggerDisableUserUnhandledExceptions] static async (context, state) =>
            {
                try
                {
                    return OutcomeO.FromResult(await state(context).ConfigureAwait(context.ContinueOnCapturedContext));
                }
                catch (Exception e)
                {
                    return OutcomeO.FromException<TResult>(e);
                }
            },
            context,
            callback).ConfigureAwait(context.ContinueOnCapturedContext);

        return outcome.GetResultOrRethrow();
    }

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the callback.</typeparam>
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public async ValueTask<TResult> ExecuteAsync<TResult, TState>(
        Func<TState, CancellationToken, ValueTask<TResult>> callback,
        TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetAsyncContext<TResult>(cancellationToken);

        try
        {
            var outcome = await Component.ExecuteAsync(
                [DebuggerDisableUserUnhandledExceptions] static async (context, state) =>
                {
                    try
                    {
                        return OutcomeO.FromResult(await state.callback(state.state, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext));
                    }
                    catch (Exception e)
                    {
                        return OutcomeO.FromException<TResult>(e);
                    }
                },
                context,
                (callback, state)).ConfigureAwait(context.ContinueOnCapturedContext);

            return outcome.GetResultOrRethrow();
        }
        finally
        {
            Pool.Return(context);
        }
    }

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public async ValueTask<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetAsyncContext<TResult>(cancellationToken);

        try
        {
            var outcome = await Component.ExecuteAsync(
                [DebuggerDisableUserUnhandledExceptions] static async (context, state) =>
                {
                    try
                    {
                        return OutcomeO.FromResult(await state(context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext));
                    }
                    catch (Exception e)
                    {
                        return OutcomeO.FromException<TResult>(e);
                    }
                },
                context,
                callback).ConfigureAwait(context.ContinueOnCapturedContext);

            return outcome.GetResultOrRethrow();
        }
        finally
        {
            Pool.Return(context);
        }
    }

    private ResilienceContextO GetAsyncContext<TResult>(CancellationToken cancellationToken)
    {
        var context = Pool.Rent(cancellationToken);

        InitializeAsyncContext<TResult>(context);

        return context;
    }

    private void InitializeAsyncContext<TResult>(ResilienceContextO context)
    {
        DisposeHelper.EnsureNotDisposed();

        context.Initialize<TResult>(isSynchronous: false);
    }
}
