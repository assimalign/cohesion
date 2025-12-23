using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

public sealed partial class ResiliencePipeline : IResiliencePipeline
{
    private readonly ResilienceContextPool _contextPool;
    private readonly ResilienceStrategy _strategy;

    internal ResiliencePipeline(ResiliencePipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _strategy = options.Strategy;
        _contextPool = contextPool;
    }

    public ResilienceStrategy Strategy => _strategy;



    public ValueTask ExecuteAsync<TState>(
        ResiliencePipelineCallback<TState> callback,
        TState state)
    {
        ResilienceContext context = _contextPool.Rent(false);

        try
        {
            return (this as IResiliencePipeline).ExecuteAsync<TState>(
                callback,
                context,
                state);
        }
        finally
        {
            _contextPool.Return(context);
        }
    }

    public ValueTask<TResult> ExecuteAsync<TResult, TState>(
        ResiliencePipelineCallback<TResult, TState> callback,
        TState state)
    {
        ResilienceContext context = _contextPool.Rent(false);

        try
        {
            return (this as IResiliencePipeline).ExecuteAsync<TResult, TState>(
                callback,
                context,
                state);
        }
        finally
        {
            _contextPool.Return(context);
        }
    }


    IResilienceStrategy IResiliencePipeline.Strategy => Strategy;
    ValueTask IResiliencePipeline.ExecuteAsync<TState>(
        ResiliencePipelineCallback<TState> callback,
        IResilienceContext context,
        TState state)
    {
        return Strategy.ExecuteAsync<TState>(
            async (context, state) =>
            {
                Outcome outcome;

                try
                {
                    await callback.Invoke(context, state);
                }
                catch (Exception exception)
                {
                    outcome = exception;
                }

                return (outcome = true);
            },
            context,
            state);
    }

    ValueTask<TResult> IResiliencePipeline.ExecuteAsync<TResult, TState>(
        ResiliencePipelineCallback<TResult, TState> callback,
        IResilienceContext context,
        TState state)
    {
        return Strategy.ExecuteAsync<TResult, TState>(
            async (context, state) =>
            {
                Outcome<TResult> outcome;

                try
                {
                    outcome = await callback.Invoke(context, state);
                }
                catch (Exception exception)
                {
                    outcome = exception;
                }

                if (outcome.If(out TResult ifTrueREsult, out Exception ifNotTrueREsult))
                {

                }
                else
                {

                }

                    return outcome;
            },
            context,
            state);
    }
}