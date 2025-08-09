using System;
using System.Net;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Transports;
using Assimalign.Cohesion.Http.Internal;

using Assimalign.Cohesion.Internal;

public sealed class HttpConnectionFactory  : IHttpConnectionFactory
{
    public HttpConnectionFactory(HttpConnectionOptions options)
    {
    }

    public Task<IHttpConnection> CreateAsync(ITransportConnection transportConnection, CancellationToken cancellationToken = default)
    {
        switch (transportConnection)
        {
            case ISingleStreamTransportConnection stream 
            when stream.Protocol == ProtocolType.Tcp:
                return CreateHttp1ConnectionAsync(stream, cancellationToken);

            case ISingleStreamTransportConnection stream 
            when stream.Protocol != ProtocolType.Tcp:
                throw new NotSupportedException($"The transport connection of type '{transportConnection.GetType().FullName}' is not supported.");

            case IMultiplexTransportConnection multiplex 
            when multiplex.Protocol == ProtocolType.Quic:
                return CreateHttp2Or3ConnectionAsync(multiplex, cancellationToken);
            
            default:
                throw new NotSupportedException($"The transport connection of type '{transportConnection.GetType().FullName}' is not supported.");
        }
    }


    private async Task<IHttpConnection> CreateHttp1ConnectionAsync(ISingleStreamTransportConnection transportConnection, CancellationToken cancellationToken = default)
    {
        if (transportConnection.IsOpen())
        {
            ThrowHelper.ThrowArgumentException("The connection is already open.");
        }

        var context = await transportConnection.OpenAsync(cancellationToken);

        var result = await context.Pipe.PeekAsync(cancellationToken);

        result.Buffer
        
        throw new NotImplementedException();
    }

    private async Task<IHttpConnection> CreateHttp2Or3ConnectionAsync(IMultiplexTransportConnection transportConnection, CancellationToken cancellationToken = default)
    {
        // Can assume HTTP/3 if over QUIC
        if (transportConnection.Protocol != ProtocolType.Quic)
        {
            throw new NotSupportedException($"The transport connection of type '{transportConnection.GetType().FullName}' is not supported.");
        }
        return new Http3Connection();
    }
}
