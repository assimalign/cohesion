
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports.Internal;

public class TransportPipelineBuilder<TConnection, TContext> : ITransportPipelineBuilder
    where TConnection : ITransportConnection
    where TContext : ITransportConnectionContext
{
    private readonly List<Func<TransportMiddleware, TransportMiddleware>> _middleware;

    public TransportPipelineBuilder()
    {
        _middleware = new List<Func<TransportMiddleware, TransportMiddleware>>();
    }

    public TransportPipelineBuilder<TConnection, TContext> Use(Func<TConnection, TContext, TransportMiddleware, Task> middleware)
    {
        ThrowHelper.ThrowIfNull(middleware);

        Func<TConnection, TContext, TransportMiddleware, Task> middleware2 = middleware;

        (this as ITransportPipelineBuilder).Use((TransportMiddleware next) => (ITransportConnection c, ITransportConnectionContext cc) =>
        {
            if (c is TConnection connection && cc is TContext context)
            {
                return middleware2.Invoke(connection, context, next);
            }

            return Task.CompletedTask;
        });

        return this;
    }

    ITransportPipelineBuilder ITransportPipelineBuilder.Use(Func<TransportMiddleware, TransportMiddleware> middleware)
    {
        ThrowHelper.ThrowIfNull(middleware);

        _middleware.Add(middleware);

        return this;
    }
    ITransportPipeline ITransportPipelineBuilder.Build()
    {
        var middleware = new TransportMiddleware((connection, context) =>
        {
            return Task.CompletedTask;
        });

        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            middleware = _middleware[i].Invoke(middleware);
        }

        return new TransportPipeline(middleware);
    }
}
