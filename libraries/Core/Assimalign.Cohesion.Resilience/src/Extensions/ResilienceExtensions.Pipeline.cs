using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public static partial class ResilienceExtensions
{
    /// <summary>
    /// Executes the specified callback within the current resilience pipeline.
    /// </summary>
    /// <typeparam name="TState">The type of state passed to the callback.</typeparam>
    /// <param name="pipeline">The pipeline used to execute the callback.</param>
    /// <param name="callback">The callback to execute.</param>
    /// <param name="state">The optional callback state.</param>
    public static void Execute<TState>(
        this IResiliencePipeline pipeline,
        Action<IResilienceContext, TState?> callback,
        TState? state = default)
    {
        pipeline.ExecuteAsync<TState>((context, value) =>
        {
            callback.Invoke(context, value);
            return ValueTask.CompletedTask;
        }, state)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Executes the specified asynchronous callback within the current resilience pipeline.
    /// </summary>
    /// <typeparam name="TState">The type of state passed to the callback.</typeparam>
    /// <param name="pipeline">The pipeline used to execute the callback.</param>
    /// <param name="callback">The callback to execute.</param>
    /// <param name="state">The optional callback state.</param>
    /// <returns>A task that represents the pipeline execution.</returns>
    public static async ValueTask ExecuteAsync<TState>(
        this IResiliencePipeline pipeline,
        Func<IResilienceContext, TState?, ValueTask> callback,
        TState? state = default)
    {
        ResilienceContext context = ResilienceContextPool.Shared.Rent(false);

        try
        {
            await pipeline.ExecuteAsync(
                (resilienceContext, value) => callback.Invoke(resilienceContext, (TState?)value),
                context,
                state).ConfigureAwait(false);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Executes the specified callback within the current generic resilience pipeline.
    /// </summary>
    /// <typeparam name="TResult">The type returned by the callback.</typeparam>
    /// <typeparam name="TState">The type of state passed to the callback.</typeparam>
    /// <param name="pipeline">The pipeline used to execute the callback.</param>
    /// <param name="callback">The callback to execute.</param>
    /// <param name="state">The optional callback state.</param>
    /// <returns>The callback result.</returns>
    public static TResult Execute<TResult, TState>(
        this IResiliencePipeline<TResult> pipeline,
        Func<IResilienceContext, TState?, TResult> callback,
        TState? state = default)
    {
        return pipeline.ExecuteAsync<TResult, TState>((context, value) => ValueTask.FromResult(callback.Invoke(context, value)), state)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Executes the specified asynchronous callback within the current generic resilience pipeline.
    /// </summary>
    /// <typeparam name="TResult">The type returned by the callback.</typeparam>
    /// <typeparam name="TState">The type of state passed to the callback.</typeparam>
    /// <param name="pipeline">The pipeline used to execute the callback.</param>
    /// <param name="callback">The callback to execute.</param>
    /// <param name="state">The optional callback state.</param>
    /// <returns>A task that represents the callback result.</returns>
    public static async ValueTask<TResult> ExecuteAsync<TResult, TState>(
        this IResiliencePipeline<TResult> pipeline,
        Func<IResilienceContext, TState?, ValueTask<TResult>> callback,
        TState? state = default)
    {
        ResilienceContext context = ResilienceContextPool.Shared.Rent(false);

        try
        {
            return await pipeline.ExecuteAsync(
                (resilienceContext, value) => callback.Invoke(resilienceContext, (TState?)value),
                context,
                state).ConfigureAwait(false);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
