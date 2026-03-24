f
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports.Internal;

/// <summary>
/// Builds a transport pipeline for a specific connection and context pairing.
/// </summary>
/// <typeparam name="TConnection">The transport connection type accepted by the pipeline.</typeparam>
/// <typeparam name="TContext">The transport context type accepted by the pipeline.</typeparam>
public class TransportPipelineBuilder<TConnection, TContext> : ITransportPipelineBuilder
    where TConnection : ITransportConnection
    where TContext : ITransportConnectionContext
{
    private readonly List<Func<TransportMiddleware, TransportMiddleware>> _middleware;

    public TransportPipelineBuilder()
    {
        _middleware = new List<Func<TransportMiddleware, TransportMiddleware>>();
    }

    /// <summary>
    /// Adds typed middleware to the transport pipeline.
    /// </summary>
    /// <param name="middleware">The middleware to add to the pipeline.</param>
    /// <returns>The current pipeline builder.</returns>
    public TransportPipelineBuilder<TConnection, TContext> Use(
        Func<TConnection, TContext, TransportMiddleware, CancellationToken, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        Func<TConnection, TContext, TransportMiddleware, CancellationToken, Task> middleware2 = middleware;

        (this as ITransportPipelineBuilder).Use((TransportMiddleware next) => (
            ITransportConnection c,
            ITransportConnectionContext cc,
            CancellationToken cancellationToken) =>
        {
            if (c is TConnection connection && cc is TContext context)
            {
                return middleware2.Invoke(connection, context, next, cancellationToken);
            }

            return Task.FromException(CreateTypeMismatchException(c, cc));
        });

        return this;
    }

    private static TransportPipelineConfigurationException CreateTypeMismatchException(
        ITransportConnection connection,
        ITransportConnectionContext context)
    {
        return new TransportPipelineConfigurationException(
            $"The transport pipeline was configured for connection type '{typeof(TConnection).FullName}' and context type '{typeof(TContext).FullName}', " +
            $"but received connection type '{connection.GetType().FullName}' and context type '{context.GetType().FullName}'.");
    }

    ITransportPipelineBuilder ITransportPipelineBuilder.Use(Func<TransportMiddleware, TransportMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware.Add(middleware);

        return this;
    }
    ITransportPipeline ITransportPipelineBuilder.Build()
    {
        var middleware = new TransportMiddleware((connection, context, cancellationToken) =>
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
