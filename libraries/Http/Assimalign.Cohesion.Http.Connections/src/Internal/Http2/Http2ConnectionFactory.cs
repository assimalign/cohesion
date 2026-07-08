using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Produces <see cref="Http2Connection"/> instances, capturing the shared
/// HTTP/2 abuse limits and request/response interceptors the listener applies
/// to every HTTP/2 connection.
/// </summary>
internal sealed class Http2ConnectionFactory : HttpConnectionFactory
{
    private readonly Http2ConnectionListenerOptions.Http2Limits _limits;
    private readonly IHttpRequestInterceptor[] _requestInterceptors;
    private readonly IHttpResponseInterceptor[] _responseInterceptors;

    public Http2ConnectionFactory(
        Http2ConnectionListenerOptions.Http2Limits limits,
        IHttpRequestInterceptor[] requestInterceptors,
        IHttpResponseInterceptor[] responseInterceptors)
    {
        _limits = limits;
        _requestInterceptors = requestInterceptors;
        _responseInterceptors = responseInterceptors;
    }

    public override HttpConnection Create(IConnection connection, bool isSecure)
        => new Http2Connection(connection, isSecure, _limits, _requestInterceptors, _responseInterceptors);
}
