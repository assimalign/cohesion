using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// The default server's interceptor that publishes the transport's client-fault report
/// (<see cref="IHttpExchangeControl.ClientFaultStatusCode"/>) to the pipeline as an
/// <see cref="IWebClientFaultFeature"/> (#1340).
/// </summary>
/// <remarks>
/// <para>
/// The exchange control exists only in an exchange's response phase, which costs the transport a
/// response sink and a control for every exchange that takes part in it. This interceptor therefore
/// declares <see cref="HttpInterceptorScopes.Request"/> and adds itself to the response phase of an
/// exchange whose request can fault while its body is read: an HTTP/1.1 request that declares a body
/// (a <c>Transfer-Encoding</c>, or a <c>Content-Length</c> other than zero; RFC 9112 §6). Every other
/// exchange, a plain <c>GET</c> among them, keeps the transport's fast path and carries no feature.
/// </para>
/// <para>
/// HTTP/2 and HTTP/3 controls do not report client faults yet, so the interceptor leaves those
/// exchanges alone rather than pay the response phase for a feature that would always report
/// <see langword="null"/>. Widening it is part of reporting the fault on those versions.
/// </para>
/// <para>
/// Both hooks are CPU-only, as the interceptor contract requires, and the instance holds no
/// per-exchange state: the feature it installs holds the exchange's control.
/// </para>
/// </remarks>
internal sealed class WebClientFaultInterceptor : HttpExchangeInterceptor
{
    // RFC 9110 §5.6.3: a list element loses its optional whitespace, SP and HTAB, and nothing else.
    private const string OptionalWhitespace = " \t";

    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

    /// <inheritdoc />
    public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
    {
        if (context.Version == HttpVersion.Http11 && DeclaresBody(context.Headers))
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

    /// <summary>
    /// Whether the request's framing declares a message body (RFC 9112 §6): a <c>Transfer-Encoding</c>,
    /// or a <c>Content-Length</c> with any value other than zero. The transport has already rejected a
    /// framing it cannot read, so a value this check cannot parse still counts as a body.
    /// </summary>
    private static bool DeclaresBody(HttpHeaderCollection headers)
    {
        if (headers.ContainsKey(HttpHeaderKey.TransferEncoding))
        {
            return true;
        }

        if (!headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue contentLength))
        {
            return false;
        }

        foreach (string? entry in contentLength)
        {
            if (entry is null)
            {
                continue;
            }

            ReadOnlySpan<char> list = entry.AsSpan();

            foreach (Range range in list.Split(','))
            {
                ReadOnlySpan<char> segment = list[range].Trim(OptionalWhitespace);

                if (!segment.IsEmpty && !segment.SequenceEqual("0"))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
