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
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public void Execute<TState>(
        Action<ResilienceContextO, TState> callback, 
        ResilienceContextO context, 
        TState state)
        => Execute(
            static (context, state) =>
            {
                state.callback(context, state.state);
                return VoidResult.Instance;
            },
            context,
            (callback: ArgumentNullException.ThrowIfNull<Action<ResilienceContextO, TState>>(callback), state));

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="context">The context associated with the callback.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public void Execute(
        Action<ResilienceContextO> callback, 
        ResilienceContextO context)
        => Execute(
            static (context, state) =>
            {
                state(context);
                return VoidResult.Instance;
            },
            context,
            ArgumentNullException.ThrowIfNull<Action<ResilienceContextO>>(callback));

    /// <summary>
    /// Executes the specified callback.
    /// </summary>
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> associated with the callback.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public void Execute<TState>(
        Action<TState, CancellationToken> callback,
        TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetSyncContext(cancellationToken);

        try
        {
            Component.Execute(
                static (context, state) =>
                {
                    state.callback(state.state, context.CancellationToken);
                    return VoidResult.Instance;
                },
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
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> associated with the callback.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public void Execute(
        Action<CancellationToken> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetSyncContext(cancellationToken);

        try
        {
            Component.Execute(
                static (context, state) =>
                {
                    state(context.CancellationToken);
                    return VoidResult.Instance;
                },
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
    /// <typeparam name="TState">The type of state associated with the callback.</typeparam>
    /// <param name="callback">The user-provided callback.</param>
    /// <param name="state">The state associated with the callback.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public void Execute<TState>(
        Action<TState> callback,
        TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetSyncContext(CancellationToken.None);

        try
        {
            Component.Execute(
                static (_, state) =>
                {
                    state.callback(state.state);
                    return VoidResult.Instance;
                },
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
    /// <param name="callback">The user-provided callback.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is <see langword="null"/>.</exception>
    public void Execute(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var context = GetSyncContext(CancellationToken.None);

        try
        {
            Component.Execute(
                static (_, state) =>
                {
                    state();
                    return VoidResult.Instance;
                },
                context,
                callback);
        }
        finally
        {
            Pool.Return(context);
        }
    }

    private ResilienceContextO GetSyncContext(CancellationToken cancellationToken) => GetSyncContext<VoidResult>(cancellationToken);
}
