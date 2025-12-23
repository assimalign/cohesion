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
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public async ValueTask ExecuteAsync<TState>(
        Func<ResilienceContextO, TState, ValueTask> callback,
        ResilienceContextO context,
        TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        InitializeAsyncContext(context);

        var outcome = await Component.ExecuteAsync(
            [DebuggerDisableUserUnhandledExceptions] static async (context, state) =>
            {
                try
                {
                    await state.callback(context, state.state).ConfigureAwait(context.ContinueOnCapturedContext);
                    return OutcomeO.Void;
                }
                catch (Exception e)
                {
                    return OutcomeO.FromException(e);
                }
            },
            context,
            (callback, state)).ConfigureAwait(context.ContinueOnCapturedContext);

        outcome.GetResultOrRethrow();
    }

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public async ValueTask ExecuteAsync(
        Func<ResilienceContextO, ValueTask> callback,
        ResilienceContextO context)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        InitializeAsyncContext(context);

        var outcome = await Component.ExecuteAsync(
            [DebuggerDisableUserUnhandledExceptions] static async (context, state) =>
            {
                try
                {
                    await state(context).ConfigureAwait(context.ContinueOnCapturedContext);
                    return OutcomeO.Void;
                }
                catch (Exception e)
                {
                    return OutcomeO.FromException(e);
                }
            },
            context,
            callback).ConfigureAwait(context.ContinueOnCapturedContext);

        outcome.GetResultOrRethrow();
    }

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents an asynchronous callback.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public async ValueTask ExecuteAsync<TState>(
        Func<TState, CancellationToken, ValueTask> callback,
        TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetAsyncContext(cancellationToken);

        try
        {
            var outcome = await Component.ExecuteAsync(
                [DebuggerDisableUserUnhandledExceptions] static async (context, state) =>
                {
                    try
                    {
                        await state.callback(state.state, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
                        return OutcomeO.Void;
                    }
                    catch (Exception e)
                    {
                        return OutcomeO.FromException(e);
                    }
                },
                context,
                (callback, state)).ConfigureAwait(context.ContinueOnCapturedContext);

            outcome.GetResultOrRethrow();
        }
        finally
        {
            Pool.Return(context);
        }
    }

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> associated with the callback.</param>
    /// <returns>The instance of <see cref="ValueTask"/> that represents an asynchronous callback.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public async ValueTask ExecuteAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetAsyncContext(cancellationToken);

        try
        {
            var outcome = await Component.ExecuteAsync(
                [DebuggerDisableUserUnhandledExceptions] static async (context, state) =>
                {
                    try
                    {
                        await state(context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
                        return OutcomeO.Void;
                    }
                    catch (Exception e)
                    {
                        return OutcomeO.FromException(e);
                    }

                },
                context,
                callback).ConfigureAwait(context.ContinueOnCapturedContext);

            outcome.GetResultOrRethrow();
        }
        finally
        {
            Pool.Return(context);
        }
    }

    private ResilienceContextO GetAsyncContext(CancellationToken cancellationToken) => GetAsyncContext<VoidResult>(cancellationToken);

    private void InitializeAsyncContext(ResilienceContextO context) => InitializeAsyncContext<VoidResult>(context);
}
