using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal sealed class HttpProtocolRegistration
{
    public HttpProtocolRegistration(HttpProtocol protocol, ServerTransport transport, bool isSecure)
    {
        Protocol = protocol;
        Transport = transport;
        IsSecure = isSecure;
    }

    public HttpProtocol Protocol { get; }

    public ServerTransport Transport { get; }

    public bool IsSecure { get; }
}
