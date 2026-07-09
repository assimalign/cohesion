using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// Produces <see cref="Http1Connection"/> instances, capturing the shared
/// server limits and request/response interceptors the listener applies to
/// every HTTP/1.1 connection.
/// </summary>
internal sealed class Http1ConnectionFactory : HttpConnectionFactory
{
    private readonly Http1ConnectionListenerOptions.Http1Limits _limits;
    private readonly IHttpExchangeInterceptor[] _interceptors;
    private readonly IHttpExchangeInterceptor[] _responseInterceptors;

    public Http1ConnectionFactory(Http1ConnectionListenerOptions.Http1Limits limits, IHttpExchangeInterceptor[] interceptors, IHttpExchangeInterceptor[] responseInterceptors)
    {
        _limits = limits;
        _interceptors = interceptors;
        _responseInterceptors = responseInterceptors;
    }

    public override HttpConnection Create(IConnection connection, bool isSecure)
        => new Http1Connection(connection, isSecure, _limits, _interceptors, _responseInterceptors);
}
