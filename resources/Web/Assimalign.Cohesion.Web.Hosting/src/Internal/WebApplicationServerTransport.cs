using Assimalign.Cohesion.Http.Transports;
using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal
{
    internal class WebApplicationServerTransport : HttpConnectionTransport
    {
        private readonly ServerTransport _server;
        private readonly HttpProtocol _httpProtocols;
        private readonly bool _isSecure;

        public WebApplicationServerTransport(TcpServerTransport server, HttpProtocol httpProtocols)
        {
            _server = server;
            _httpProtocols = httpProtocols;
            _isSecure = server.IsSecure;
        }

        public WebApplicationServerTransport(QuicServerTransport server)
        {
            _server = server;
            _httpProtocols = HttpProtocol.Http30;
            _isSecure = true;
        }

        public override bool IsSecure => _isSecure;
        public override HttpProtocol HttpProtocols => _httpProtocols;
        public override ValueTask DisposeAsync()
        {
            return _server.DisposeAsync();
        }

        protected override async Task<TransportConnection> InitializeAsync(CancellationToken cancellationToken = default)
        {
            return _server switch
            {
#pragma warning disable CA1416
                QuicServerTransport quic => await quic.AcceptOrListenAsync(cancellationToken),
#pragma warning restore CA1416
                TcpServerTransport tcp => await tcp.AcceptOrListenAsync(cancellationToken),

                _ => throw new NotSupportedException()
            };
        }
    }
}
