using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Produces <see cref="Http2Connection"/> instances, capturing the shared
/// HTTP/2 abuse limits and response interceptors the listener applies to
/// every HTTP/2 connection.
/// </summary>
internal sealed class Http2ConnectionFactory : HttpConnectionFactory
{
    private readonly Http2Limits _limits;
    private readonly IHttpResponseInterceptor[] _responseInterceptors;

    public Http2ConnectionFactory(Http2Limits limits, IHttpResponseInterceptor[] responseInterceptors)
    {
        _limits = limits;
        _responseInterceptors = responseInterceptors;
    }

    public override HttpConnection Create(IConnection connection, bool isSecure)
        => new Http2Connection(connection, isSecure, _limits, _responseInterceptors);
}
