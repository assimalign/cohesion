using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

public static partial class ResilienceExtensions
{
    extension(ResiliencePipeline pipeline)
    {
        public void Execute<TState>(
            ResiliencePipelineCallback<TState> callback,
            IResilienceContext context,
            TState state)
        {
            pipeline.ExecuteAsync(callback, context, state)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }


        public TResult Execute<TResult, TState>(
            ResiliencePipelineCallback<TResult, TState> callback,
            IResilienceContext context,
            TState state)
        {
            return pipeline.ExecuteAsync(callback, context, state)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
    }


    //extension(IResiliencePipeline pipeline)
    //{
    //    public void Execute<TState>(
    //        ResiliencePipelineCallback<ValueTask, TState> callback,
    //        IResilienceContext context,
    //        TState state)
    //    {
    //        if (pipeline == null)
    //            throw new ArgumentNullException(nameof(pipeline));
    //        if (callback == null)
    //            throw new ArgumentNullException(nameof(callback));
    //        if (context == null)
    //            throw new ArgumentNullException(nameof(context));
    //        pipeline.ExecuteAsync(callback, context, state).GetAwaiter().GetResult();
    //    }
    //}


    extension(ResiliencePipelineBuilder builder)
    {

    }
}
