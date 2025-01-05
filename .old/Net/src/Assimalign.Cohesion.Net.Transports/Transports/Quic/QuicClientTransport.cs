#if NET7_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

[RequiresPreviewFeatures]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public sealed class QuicClientTransport : ClientTransport
{
    public QuicClientTransport()
    {
            
    }
    public override ProtocolType ProtocolType => throw new NotImplementedException();

    public override TransportMiddlewareHandler Middleware => throw new NotImplementedException();

    public override async Task<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                // Client implementation: QuicConnection.ConnectAsync()
                var connection = await QuicConnection.ConnectAsync(new()
                {

                }, cancellationToken);

                var inboundStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
            }
            catch (Exception exception)
            {

            }
        }
        throw new NotImplementedException();
    }

    public override void Dispose()
    {
        throw new NotImplementedException();
    }
}
#endif
