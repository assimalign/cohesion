using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;

/// <summary>
/// An abstract builder for create a middleware pipeline.
/// </summary>
/// <typeparam name="TTransport"></typeparam>
public abstract class TransportBuilder<TTransport> : ITransportBuilder
    where TTransport : ITransport
{
    private readonly List<Func<TransportMiddleware, TransportMiddleware>> _delegates;

    protected TransportBuilder()
    {
        _delegates = new List<Func<TransportMiddleware, TransportMiddleware>>();
    }

    /// <summary>
    /// Passes the transport pipeline to the transport that needs to be configured.
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    protected abstract TTransport OnBuild(TransportMiddleware middleware);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public virtual TTransport Build()
    {
        return (TTransport)(this as ITransportBuilder).Build();
    }

    ITransport ITransportBuilder.Build()
    {
        var middleware = new TransportMiddleware(context =>
        {
            return Task.CompletedTask;
        });

        for (int i = _delegates.Count - 1; i >= 0; i--)
        {
            middleware = _delegates[i].Invoke(middleware);
        }

        return OnBuild(middleware);
    }

    ITransportBuilder ITransportBuilder.Use(Func<TransportMiddleware, TransportMiddleware> middleware)
    {
        ThrowHelper.ThrowIfNull(middleware);

        _delegates.Add(middleware);

        return this;
    }
}
