using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public static partial class ResilienceExtensions
{
    extension(IResiliencePipeline pipeline)
    {
        /// <summary>
        /// Executes the specified asynchronous callback within the current resilience pipeline, blocking until the
        /// operation completes.
        /// </summary>
        /// <remarks>This method blocks the calling thread until the asynchronous operation completes. Use
        /// this method when synchronous execution is required. For non-blocking scenarios, consider using the
        /// asynchronous alternative.</remarks>
        /// <typeparam name="TState">The type of the state object to pass to the callback.</typeparam>
        /// <param name="callback">A function that represents the asynchronous operation to execute. The function receives the current
        /// resilience context and the provided state object.</param>
        /// <param name="state">An optional state object to pass to the callback. The default value is used if not specified.</param>
        public void Execute<TState>(
            Action<IResilienceContext, TState?> callback,
            TState? state = default)
        {
            pipeline.ExecuteAsync<TState>((context, state) =>
            {
                callback.Invoke(context, state);

                return ValueTask.CompletedTask;
            }, state)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        /// <summary>
        /// Executes the specified asynchronous callback within a resilience context, allowing for custom state to be
        /// passed to the operation.
        /// </summary>
        /// <remarks>The callback is executed within a pooled resilience context, which is automatically
        /// managed for the duration of the operation. This method ensures that the context is properly returned to the
        /// pool after execution, even if an exception occurs.</remarks>
        /// <typeparam name="TState">The type of the state object to pass to the callback.</typeparam>
        /// <param name="callback">A function to execute within the resilience context. The function receives the current <see cref="IResilienceContext"/> and the provided state object.</param>
        /// <param name="state">An optional state object to pass to the callback. The value can be <see langword="null"/>.</param>
        /// <returns>A <see cref="ValueTask"/> that represents the asynchronous execution of the callback.</returns>
        public async ValueTask ExecuteAsync<TState>(
            Func<IResilienceContext, TState?, ValueTask> callback,
            TState? state = default)
        {
            ResilienceContext context = ResilienceContextPool.Shared.Rent(false);

            try
            {
                await pipeline.ExecuteAsync(
                    (context, state) => callback.Invoke(context, (TState)state!),
                    context,
                    state);
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }
    }

    extension<TResult>(IResiliencePipeline<TResult> pipeline)
    {
        public TResult Execute<TState>(
            Func<IResilienceContext, TState?, TResult> callback,
            TState? state = default)
        {
            Func<IResilienceContext, TState?, ValueTask<TResult>> callback1 = (context, state) =>
            {
                return ValueTask.FromResult(callback.Invoke(context, state));
            };

            return pipeline.ExecuteAsync(callback1, state)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        public async ValueTask<TResult> ExecuteAsync<TState>(
           Func<IResilienceContext, TState?, ValueTask<TResult>> callback,
           TState? state = default)
        {
            ResilienceContext context = ResilienceContextPool.Shared.Rent(false);

            try
            {
                return await pipeline.ExecuteAsync(
                    (context, state) => callback.Invoke(context, (TState)state!),
                    context,
                    state);
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }
    }

    

    //private static ResilienceContextPool _pool = ResilienceContextPool.Shared;


    //extension(IResilienceContext context)
    //{
    //    public IResilienceContext Copy<TContext>() where TContext : IResilienceContext, IDisposable
    //    {
    //        var pooledContext = _pool.Rent();

    //        return new DisposableResilienceContext(context =>
    //        {
    //            _pool.Return(context);

    //        }, pooledContext);

    //    }

    //    private void Test()
    //    {
    //        var context = default(IResilienceContext);

    //        context.Copy()

    //    }
    //}



    //public async ValueTask ExecuteAsync(
    //    ResilienceCallback callback,
    //    object? state = null)
    //{
    //    ResilienceContext context = ResilienceContextPool.Shared.Rent(false);

    //    try
    //    {
    //        await pipeline.ExecuteAsync(
    //            callback,
    //            context,
    //            state);
    //    }
    //    finally
    //    {
    //        ResilienceContextPool.Shared.Return(context);
    //    }
    //}

    ///// <summary>
    ///// Executes the specified resilience callback asynchronously within the context of the provided operation key.
    ///// </summary>
    ///// <remarks>This method manages the execution context for the operation and ensures proper
    ///// resource handling. The callback is executed within a resilience pipeline, which may apply policies such as
    ///// retries or circuit breakers depending on configuration.</remarks>
    ///// <param name="operationKey">The key that uniquely identifies the operation to be executed. Used to associate execution context and
    ///// resources with the operation.</param>
    ///// <param name="callback">The delegate representing the resilience operation to execute. This callback is invoked within the managed
    ///// execution context.</param>
    ///// <param name="state">An optional state object to pass to the callback. May be null if no state is required.</param>
    ///// <returns>A ValueTask that represents the asynchronous execution of the callback operation.</returns>
    //public async ValueTask ExecuteAsync(
    //    ResilienceCallback callback,
    //    OperationKey operationKey,
    //    object? state = null)
    //{
    //    ResilienceContext context = _pool.Rent(operationKey);

    //    try
    //    {
    //        await pipeline.ExecuteAsync(
    //            callback,
    //            context,
    //            state);
    //    }
    //    finally
    //    {
    //        _pool.Return(context);
    //    }
    //}


    //public async ValueTask ExecuteAsync(
    //    ResilienceCallback callback,
    //    bool continueOnCapturedContext,
    //    object? state = null)
    //{
    //    ResilienceContext context = _pool.Rent(continueOnCapturedContext);

    //    try
    //    {
    //        await pipeline.ExecuteAsync(
    //            callback,
    //            context,
    //            state);
    //    }
    //    finally
    //    {
    //        _pool.Return(context);
    //    }
    //}

    //public async ValueTask ExecuteAsync(
    //    ResilienceCallback callback,
    //    CancellationToken cancellationToken,
    //    object? state = null)
    //{
    //    ResilienceContext context = _pool.Rent(cancellationToken);

    //    try
    //    {
    //        await pipeline.ExecuteAsync(
    //            callback,
    //            context,
    //            state);
    //    }
    //    finally
    //    {
    //        _pool.Return(context);
    //    }
    //}

    //public async ValueTask ExecuteAsync(
    //    ResilienceCallback callback,
    //    OperationKey operationKey,
    //    bool continueOnCapturedContext,
    //    CancellationToken cancellationToken,
    //    object? state = null)
    //{
    //    ResilienceContext context = _pool.Rent(operationKey, continueOnCapturedContext, cancellationToken);

    //    try
    //    {
    //        await pipeline.ExecuteAsync(
    //            callback,
    //            context,
    //            state);
    //    }
    //    finally
    //    {
    //        _pool.Return(context);
    //    }
    //}
}
