using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Produces <see cref="Http1Connection"/> instances, capturing the shared
/// server limits, request/response interceptors, and per-exchange feature
/// capacity the listener applies to every HTTP/1.1 connection.
/// </summary>
internal sealed class Http1ConnectionFactory : HttpConnectionFactory
{
    private readonly Http1ConnectionListenerOptions.Http1Limits _limits;
    private readonly IHttpExchangeInterceptor[] _interceptors;
    private readonly IHttpExchangeInterceptor[] _responseInterceptors;
    private readonly int _featureCapacity;

    public Http1ConnectionFactory(Http1ConnectionListenerOptions.Http1Limits limits, IHttpExchangeInterceptor[] interceptors, IHttpExchangeInterceptor[] responseInterceptors, int featureCapacity)
    {
        _limits = limits;
        _interceptors = interceptors;
        _responseInterceptors = responseInterceptors;
        _featureCapacity = featureCapacity;
    }

    public override HttpConnection Create(IConnection connection, bool isSecure)
        => new Http1Connection(connection, isSecure, _limits, _interceptors, _responseInterceptors, _featureCapacity, AltSvcHeaderValue);
}
