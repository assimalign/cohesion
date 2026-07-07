using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Produces <see cref="Http2Connection"/> instances, capturing the shared
/// response interceptors the listener applies to every HTTP/2 connection.
/// </summary>
internal sealed class Http2ConnectionFactory : HttpConnectionFactory
{
    private readonly IHttpResponseInterceptor[] _responseInterceptors;

    public Http2ConnectionFactory(IHttpResponseInterceptor[] responseInterceptors)
    {
        _responseInterceptors = responseInterceptors;
    }

    public override HttpConnection Create(IConnection connection, bool isSecure)
        => new Http2Connection(connection, isSecure, _responseInterceptors);
}
