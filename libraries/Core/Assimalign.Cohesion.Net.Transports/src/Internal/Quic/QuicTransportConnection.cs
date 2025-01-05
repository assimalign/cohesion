#if NET7_0_OR_GREATER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class QuicTransportConnection : ITransportConnection
{
    public QuicTransportConnection()
    {
        
    }
    public bool IsConnected => throw new NotImplementedException();

    public object? ConnectionData => throw new NotImplementedException();

    public ConnectionState State => throw new NotImplementedException();

    public ITransportConnectionPipe Pipe => throw new NotImplementedException();

    public EndPoint LocalEndPoint => throw new NotImplementedException();

    public EndPoint RemoteEndPoint => throw new NotImplementedException();

    public void Execute()
    {
        throw new NotImplementedException();
    }


    public void Abort()
    {
        throw new NotImplementedException();
    }

    public ValueTask AbortAsync()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    
}
#endif
