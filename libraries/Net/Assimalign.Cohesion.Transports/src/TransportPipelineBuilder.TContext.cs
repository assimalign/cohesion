using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Internal;

public class TransportPipelineBuilder<TContext> : ITransportPipelineBuilder where TContext : TransportConnectionContext
{
    private readonly List<Func<TransportMiddleware, TransportMiddleware>> _middleware;

    public TransportPipelineBuilder()
    {
        _middleware = new List<Func<TransportMiddleware, TransportMiddleware>>();
    }


    public virtual TransportPipelineBuilder<TContext> Use(Func<TContext, TransportMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        Func<TContext, TransportMiddleware, Task> middleware2 = middleware;

        ((ITransportPipelineBuilder)this).Use((TransportMiddleware next) => (ITransportConnectionContext context) =>
        {
            if (context is TContext typedContext)
            {
                return middleware2.Invoke(typedContext, next);
            }

            return Task.FromException(CreateTypeMismatchException(context));
        });

        return this;
    }



    public virtual TransportPipeline<TContext> Build()
    {
        TransportMiddleware pipeline = context =>
        {
            return Task.CompletedTask;
        };
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i].Invoke(pipeline);
        }

        return OnBuild(pipeline);
    }

    /// <summary>
    /// Generated the default pipeline. Override this method to provide a custom pipeline implementation.
    /// </summary>
    /// <param name="pipeline"></param>
    /// <returns></returns>
    protected virtual TransportPipeline<TContext> OnBuild(TransportMiddleware pipeline)
    {
        return new DefaultTransportPipeline<TContext>(pipeline);
    }

    ITransportPipeline ITransportPipelineBuilder.Build()
    {
        return Build();
    }
    ITransportPipelineBuilder ITransportPipelineBuilder.Use(Func<TransportMiddleware, TransportMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middleware.Add(middleware);
        return this;
    }

    private static TransportPipelineConfigurationException CreateTypeMismatchException(ITransportConnectionContext context)
    {
        return new TransportPipelineConfigurationException($"Invalid Context type '{context.GetType().FullName}'.");
    }
}