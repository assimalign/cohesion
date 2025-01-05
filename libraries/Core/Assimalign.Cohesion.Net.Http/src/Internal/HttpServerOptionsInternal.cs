
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;

internal sealed class HttpServerOptionsInternal : HttpServerOptions
{
    public IServiceProvider? ServiceProvider { get; init; }
    public IHttpContextExecutor Executor { get; init; }
    public IList<ITransport> Transports { get; set; } = new List<ITransport>();

    public override void UseTransport(ITransport transport)
    {
        ValidateTransport(transport);
        Transports.Add(transport);
    }
    public override void UseTransport(Func<ITransport> configure)
    {
        var transport = configure.Invoke();
        ValidateTransport(transport);
        Transports.Add(transport);
    }
    public override void UseTcpTransport(Action<TcpServerTransportOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new TcpServerTransportOptions();

        configure.Invoke(options);

        Transports.Add(new TcpServerTransport(options));
    }


    private void ValidateTransport(ITransport transport)
    {
        if (transport is null)
        {
            throw new ArgumentNullException(nameof(transport));
        }
        if (transport.TransportType == TransportType.Client)
        {
            throw new ArgumentException("Transport must be a server configure.", nameof(transport));
        }
        if (transport.ProtocolType != ProtocolType.Tcp && transport.ProtocolType != ProtocolType.Quic)
        {
            throw new ArgumentException("Transport must be a TCP or QUIC configure.", nameof(transport));
        }
    }

    public override void UseExecutor(IHttpContextExecutor executor)
    {
        throw new NotImplementedException();
    }
}
