using System;
using System.Threading;

namespace Assimalign.Cohesion.Resilience;

#pragma warning disable CA1031 // Do not catch general exception types
#pragma warning disable RS0027 // API with optional parameter(s) should have the most parameters amongst its public overloads

public partial class ResiliencePipelineO
{
    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the callback.</typeparam>
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <returns>An instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public TResult Execute<TResult, TState>(
        Func<ResilienceContextO, TState, TResult> callback,
        ResilienceContextO context,
        TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        InitializeSyncContext<TResult>(context);

        return Component.Execute(callback, context, state);
    }

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <returns>An instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public TResult Execute<TResult>(
        Func<ResilienceContextO, TResult> callback,
        ResilienceContextO context)
        => Execute(
            static (context, state) => state(context), 
            context, 
            ArgumentNullException.ThrowIfNull<Func<ResilienceContextO, TResult>>(callback));

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TResult">The type of result returned by the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> associated with the callback.</param>
    /// <returns>An instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public TResult Execute<TResult>(
        Func<CancellationToken, TResult> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetSyncContext<TResult>(cancellationToken);

        try
        {
            return Component.Execute(
                static (context, state) => state(context.CancellationToken),
                context,
                callback);
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
    /// <returns>An instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public TResult Execute<TResult>(Func<TResult> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetSyncContext<TResult>(CancellationToken.None);

        try
        {
            return Component.Execute(
                static (_, state) => state(),
                context,
                callback);
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
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <returns>An instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public TResult Execute<TResult, TState>(Func<TState, TResult> callback, TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetSyncContext<TResult>(CancellationToken.None);

        try
        {
            return Component.Execute(
                static (_, state) => state.callback(state.state),
                context,
                (callback, state));
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
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> associated with the callback.</param>
    /// <returns>An instance of <see cref="ValueTask"/> that represents the asynchronous execution.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public TResult Execute<TResult, TState>(
        Func<TState, CancellationToken, TResult> callback,
        TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetSyncContext<TResult>(cancellationToken);

        try
        {
            return Component.Execute(
                static (context, state) => state.callback(state.state, context.CancellationToken),
                context,
                (callback, state));
        }
        finally
        {
            Pool.Return(context);
        }
    }

    private ResilienceContextO GetSyncContext<TResult>(CancellationToken cancellationToken)
    {
        var context = Pool.Rent(cancellationToken);

        InitializeSyncContext<TResult>(context);

        return context;
    }

    private void InitializeSyncContext<TResult>(ResilienceContextO context)
    {
        DisposeHelper.EnsureNotDisposed();

        context.Initialize<TResult>(isSynchronous: true);
    }
}
