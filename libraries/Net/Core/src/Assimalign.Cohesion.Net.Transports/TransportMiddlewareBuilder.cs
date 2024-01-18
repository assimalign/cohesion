// Ignore Spelling: memoise

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Internal;
using Assimalign.Cohesion.Net.Transports.Internal;

public sealed class TransportMiddlewareBuilder<TContext, TMiddleware> : ITransportMiddlewareBuilder
    where TContext : ITransportContext
    where TMiddleware : ITransportMiddleware
{
    private readonly Queue<ITransportMiddleware> middleware = new();
    private int chainIndex;

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public TransportMiddlewareBuilder<TContext, TMiddleware> UseNext<T>() where T: TMiddleware, new()
    {
        this.middleware.Enqueue(new T());
        return this;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public TransportMiddlewareBuilder<TContext, TMiddleware> UseNext(TMiddleware middleware)
    {
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));
        }
        this.middleware.Enqueue(middleware);
        return this;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public TransportMiddlewareBuilder<TContext, TMiddleware> UseNext(Func<TContext, TransportMiddlewareHandler, Task> middleware)
    {
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));
        }
        this.middleware.Enqueue(new TransportMiddlewareDefault((context, next) =>
        {
            return middleware.Invoke((TContext)context, next);
        }));
        return this;
    }
    /// <inheritdoc />
    ITransportMiddlewareBuilder ITransportMiddlewareBuilder.UseNext(ITransportMiddleware middleware)
    {
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));
        }
        this.middleware.Enqueue(middleware);
        return this;
    }
    /// <inheritdoc />
    ITransportMiddlewareBuilder ITransportMiddlewareBuilder.UseNext(TransportMiddleware middleware)
    {
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));
        }
        this.middleware.Enqueue(new TransportMiddlewareDefault(middleware));
        return this;
    }

    /// <inheritdoc />
    public TransportMiddlewareHandler Build()
    {
        var memoise = Cacher<ITransportMiddlewareBuilder, TransportMiddlewareHandler>.Memoise(builder =>
        {
            var root = new TransportMiddlewareHandler(context => Task.CompletedTask);

            return middleware.Count == 0 ? root : Build(root);
        });
        return memoise.Invoke(this);
    }

    private TransportMiddlewareHandler Build(TransportMiddlewareHandler handler)
    {
        var middleware = this.middleware.Reverse().Skip(chainIndex).First();
        var next = new TransportMiddlewareHandler(context =>
        {
            return middleware.InvokeAsync(context, handler);
        });
        if (chainIndex < this.middleware.Count - 1)
        {
            chainIndex++;
            return Build(next);
        }
        chainIndex = 0;
        return next;
    }
}
