using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// An interceptor that records the connection info its request-parse hook
/// (<see cref="AfterRequestHead"/>) and its response hook (<see cref="BeforeResponse"/>) observe, and
/// the TLS handshake facet each one finds on it.
/// </summary>
internal sealed class TlsObservingInterceptor : HttpExchangeInterceptor
{
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.All;

    public HttpConnectionInfo? ConnectionInfoAtRequestHead { get; private set; }

    public ITlsConnectionInfo? TlsAtRequestHead { get; private set; }

    public HttpConnectionInfo? ConnectionInfoBeforeResponse { get; private set; }

    public ITlsConnectionInfo? TlsBeforeResponse { get; private set; }

    public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
    {
        ConnectionInfoAtRequestHead = context.ConnectionInfo;
        TlsAtRequestHead = context.ConnectionInfo as ITlsConnectionInfo;
    }

    public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
    {
        ConnectionInfoBeforeResponse = context.ConnectionInfo;
        TlsBeforeResponse = context.ConnectionInfo as ITlsConnectionInfo;
    }
}
