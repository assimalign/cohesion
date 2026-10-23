using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Produces <see cref="Http1Connection"/> instances, capturing the shared
/// server limits, request/response interceptors, and per-exchange feature
/// capacity the listener applies to every HTTP/1.1 connection.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("Http1ConnectionFactory")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    public override HttpConnection Create(IDuplexConnection connection, bool isSecure)
        => new Http1Connection(connection, isSecure, _limits, _interceptors, _responseInterceptors, _featureCapacity, AltSvcHeaderValue, ResponseHeaderPolicy);

    private sealed class DebugView
    {
        private readonly Http1ConnectionFactory _target;

        public DebugView(Http1ConnectionFactory value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Http.Connections.Internal.HttpResponseHeaderPolicy ResponseHeaderPolicy => _target.ResponseHeaderPolicy;
        public string? AltSvcHeaderValue => _target.AltSvcHeaderValue;
    }
}
