namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The exchange interceptor that makes extended CONNECT (RFC 8441, RFC 9220) available on an
/// exchange. One stateless instance takes part in the request phase of every exchange, and in the
/// response phase of the extended CONNECT exchanges only:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item><description><see cref="AfterRequestHead"/> recognizes an HTTP/2 or HTTP/3
///   <c>CONNECT</c> whose <c>:protocol</c> the transport validated
///   (<see cref="HttpExchangeInterceptorRequestContext.Protocol"/>), installs an unbound
///   <see cref="HttpExtendedConnectFeature"/> for it, and adds itself to that exchange's response
///   phase (<see cref="HttpExchangeInterceptorRequestContext.AddResponseInterceptor"/>). Installing
///   the feature at head time keeps it visible to every later hook, including the listener's own
///   response interceptors, which run before the ones an exchange adds.</description></item>
///   <item><description><see cref="BeforeResponse"/> binds the feature to the transport's exchange
///   control when the control can accept the tunnel
///   (<see cref="IHttpExchangeControl.CanAcceptTunnel"/>), and otherwise removes it, so
///   <c>context.ExtendedConnect</c> never surfaces a feature whose accept could not
///   work.</description></item>
/// </list>
/// <para>
/// The scope is <see cref="HttpInterceptorScopes.Request"/>: a response-scoped interceptor makes the
/// transport build a response sink and an exchange control for every exchange on every protocol,
/// and the tunnel needs them only for the extended CONNECT exchanges. Every other exchange keeps the
/// transport's fast path, which matters because the Web host registers this interceptor by default.
/// </para>
/// <para>
/// Both hooks are CPU-only per the interceptor contract (on HTTP/2 they run on the connection's frame
/// pump), and all per-request state lives in the exchange's feature collection, never in instance
/// fields.
/// </para>
/// </remarks>
internal sealed class HttpExtendedConnectInterceptor : HttpExchangeInterceptor
{
    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

    /// <inheritdoc />
    public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
    {
        // HTTP/1.1 has no extended CONNECT: its CONNECT and upgrades take the whole connection over
        // (Http.ProtocolUpgrade). The transport sets Protocol only on a CONNECT it validated against
        // RFC 8441 §4 / RFC 9220 §3; the version and method checks keep a hand-built context honest.
        if (context.Version is not (HttpVersion.Http20 or HttpVersion.Http30)
            || context.Method != HttpMethod.Connect
            || context.Protocol is not { Length: > 0 } protocol)
        {
            return;
        }

        context.Features.Set(new HttpExtendedConnectFeature(protocol));
        context.AddResponseInterceptor(this);
    }

    /// <inheritdoc />
    public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
    {
        if (context.Features.Get(HttpExtendedConnectFeature.FeatureName) is not HttpExtendedConnectFeature feature)
        {
            return;
        }

        // Defensive: the HTTP/2 and HTTP/3 controls accept a validated extended CONNECT whose
        // response has not started, but an exchange whose control cannot (a hand-built context with no
        // control, or one already cancelled) degrades to "no extended CONNECT" rather than surfacing
        // a feature whose accept could never work.
        if (context.Control is { CanAcceptTunnel: true } control)
        {
            feature.Bind(control);
            return;
        }

        context.Features.Remove(HttpExtendedConnectFeature.FeatureName);
    }
}
