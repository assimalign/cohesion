using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Transports;
using Assimalign.Cohesion.Http.Internal;
using System.Net;

public sealed class HttpConnectionFactory
{
    public HttpConnectionFactory(HttpConnectionOptions options)
    {
        
    }

    public IHttpConnection Create(ITransportConnection connection)
    {
        // Can assume HTTP/3 if over QUIC
        if (connection.Protocol == ProtocolType.Quic)
        {
            return new Http3Connection();
        }
        
        //connection.Pipe.REa


        return default;
    }
}
