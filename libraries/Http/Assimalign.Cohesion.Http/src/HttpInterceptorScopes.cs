using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Declares which phases of an exchange an <see cref="IHttpExchangeInterceptor"/> participates in.
/// The server transport reads this once, when it snapshots the registered interceptors, and invokes
/// an interceptor only for the phases it declared — which is what preserves the transport's
/// zero-cost fast paths (no response sink or exchange control is ever constructed for an exchange
/// whose registered interceptors are all request-scoped and whose request-parse hooks added no
/// response interceptor to it, and no request-parse interception state is allocated when none are
/// request-scoped).
/// </summary>
[Flags]
public enum HttpInterceptorScopes
{
    /// <summary>
    /// The interceptor participates in no phase. It is registered but never invoked.
    /// </summary>
    None = 0,

    /// <summary>
    /// The interceptor participates in the request-parse phase:
    /// <see cref="IHttpExchangeInterceptor.AfterRequestHead"/>,
    /// <see cref="IHttpExchangeInterceptor.BeforeRequestBody"/>, and
    /// <see cref="IHttpExchangeInterceptor.AfterRequestBody"/>.
    /// </summary>
    Request = 1,

    /// <summary>
    /// The interceptor participates in the response lifecycle:
    /// <see cref="IHttpExchangeInterceptor.BeforeResponse"/>,
    /// <see cref="IHttpExchangeInterceptor.BeforeResponseHeadAsync"/>, and
    /// <see cref="IHttpExchangeInterceptor.AfterResponseAsync"/>, on every exchange. Declaring this
    /// scope is what causes the transport to construct the per-exchange raw response body sink and
    /// exchange control it exposes on <see cref="HttpExchangeInterceptorResponseContext"/>, for
    /// every exchange. An interceptor that needs the response phase for some exchanges only declares
    /// <see cref="Request"/> instead and adds itself to those exchanges from a request-parse hook
    /// (<see cref="HttpExchangeInterceptorRequestContext.AddResponseInterceptor"/>).
    /// </summary>
    Response = 2,

    /// <summary>
    /// The interceptor participates in both phases.
    /// </summary>
    All = Request | Response,
}
