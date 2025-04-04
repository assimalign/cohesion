using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;

public sealed class TcpClientTransportBuilder : TransportBuilder<TcpClientTransport>
{
    private readonly Func<TransportMiddleware, TcpClientTransport> _onBuild;

    internal TcpClientTransportBuilder(Func<TransportMiddleware, TcpClientTransport> onBuild)
    {
        _onBuild = onBuild;
    }

    public TcpClientTransportBuilder Use(Func<TcpTransportContext, TransportMiddleware, Task> middleware)
    {
        ThrowHelper.ThrowIfNull(middleware);

        Func<TcpTransportContext, TransportMiddleware, Task> middleware2 = middleware;

        (this as ITransportBuilder).Use((TransportMiddleware next) => (context) =>
        {
            if (context is TcpTransportContext c)
            {
                return middleware2.Invoke(c, next);
            }

            return Task.CompletedTask;
        });

        return this;
    }

    protected override TcpClientTransport OnBuild(TransportMiddleware middleware)
    {
        return _onBuild.Invoke(middleware);
    }
}
