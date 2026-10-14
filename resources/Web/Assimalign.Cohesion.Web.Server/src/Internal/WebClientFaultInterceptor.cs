using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>
/// The default server's interceptor that publishes the transport's client-fault report
/// (<see cref="IHttpExchangeControl.ClientFaultStatusCode"/>) to the pipeline as an
/// <see cref="IWebClientFaultFeature"/> (#1340, #1378).
/// </summary>
/// <remarks>
/// <para>
/// The exchange control exists only in an exchange's response phase, which costs the transport a
/// response sink and a control for every exchange that takes part in it. This interceptor therefore
/// declares <see cref="HttpInterceptorScopes.Request"/> and adds itself to the response phase of an
/// exchange whose request can fault while its body is read: one that can have a body
/// (<see cref="HttpExchangeInterceptorRequestContext.CanHaveBody"/>). The transport decides that from the
/// framing only it sees: on HTTP/1.1 a <c>Transfer-Encoding</c> or a <c>Content-Length</c> above zero, on
/// HTTP/2 a head that did not end the stream, on HTTP/3 a request stream that had not ended with its
/// head, and never for a <c>CONNECT</c>. An HTTP/2 or HTTP/3 request need not declare its body, so the
/// framing fields of the head cannot answer it there. Every other exchange, a plain <c>GET</c> among them,
/// carries no client-fault feature. The QUIC driver's input-completion facet exposes a FIN it already
/// holds. HTTP/3 still retains the gate for buffered bytes or a positive declared content length, whose
/// missing body must report a truncation fault. Other interceptors can independently require a response phase.
/// </para>
/// <para>
/// Both hooks are CPU-only, as the interceptor contract requires, and the instance holds no
/// per-exchange state: the feature it installs holds the exchange's control.
/// </para>
/// </remarks>
internal sealed class WebClientFaultInterceptor : HttpExchangeInterceptor
{
    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

    /// <inheritdoc />
    public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
    {
        if (context.CanHaveBody)
        {
            context.AddResponseInterceptor(this);
        }
    }

    /// <inheritdoc />
    public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
    {
        // A hand-built context without a transport behind it carries no control: nothing reports a fault.
        if (context.Control is { } control)
        {
            context.Features.Set(new WebClientFaultFeature(control));
        }
    }
}
