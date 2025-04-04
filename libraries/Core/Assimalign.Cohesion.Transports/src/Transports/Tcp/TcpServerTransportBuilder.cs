using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports.Internal;

public sealed class TcpServerTransportBuilder: TransportBuilder<TcpServerTransport>
{
    private readonly Func<TransportMiddleware, TcpServerTransport> _onBuild;

    internal TcpServerTransportBuilder(Func<TransportMiddleware, TcpServerTransport> onBuild)
    {
        _onBuild = onBuild;
    }

    public TcpServerTransportBuilder Use(Func<TcpTransportContext, TransportMiddleware, Task> middleware)
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

    protected override TcpServerTransport OnBuild(TransportMiddleware middleware)
    {
        return _onBuild.Invoke(middleware);
    }
}
