using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal;

internal sealed class HttpProtocolRegistration
{
    public HttpProtocolRegistration(HttpProtocol protocol, ITransport transport, bool isSecure)
    {
        Protocol = protocol;
        Transport = transport;
        IsSecure = isSecure;
    }

    public HttpProtocol Protocol { get; }

    public ITransport Transport { get; }

    public bool IsSecure { get; }
}
