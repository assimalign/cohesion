using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

internal class Http1ConnectionListener : HttpConnectionListener<Http1Connection>
{
    private readonly TcpServerTransport _transport;

    public Http1ConnectionListener()
    {
        
    }


    public override ProtocolType Protocol => throw new NotImplementedException();

    public override async Task<Http1Connection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _transport.AcceptOrListenAsync(cancellationToken);
        var context = await connection.OpenAsync(cancellationToken);


        throw new NotImplementedException();
    }

    public override ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }
}

