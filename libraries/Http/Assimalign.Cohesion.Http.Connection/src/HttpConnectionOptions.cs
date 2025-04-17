
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

using Transports;
using Cohesion.Internal;

public class HttpConnectionOptions
{


    public void UseTransport(ServerTransport transport)
    {
        
    }

    public void UseTransport<T>(ServerTransport<T> transport) where T : ITransportConnection
    {
        if (transport.Kind != TransportKind.Server)
        {
            ThrowHelper.ThrowArgumentException("");
        }
    }

    public TcpServerTransport UseTcpTransport(Action<TcpServerTransportOptions> configure)
    {
        return TcpServerTransport.Create(configure);
    }
}
