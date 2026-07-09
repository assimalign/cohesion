using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The guided implementation base for <see cref="IHttpExchangeInterceptor"/>. Every lifecycle hook
/// is a <see langword="virtual"/> no-op, so an implementation derives from this base and overrides
/// <b>only</b> the hooks it participates in — opting into exactly the API it needs.
/// </summary>
/// <remarks>
/// <para>
/// The interface remains the contract the transport consumes; this base is the seam's
/// compatibility surface. Future lifecycle hooks are added here as virtual no-ops (with matching
/// interface members), so implementations built on the base keep compiling as the seam grows.
/// Implementing <see cref="IHttpExchangeInterceptor"/> directly is permitted but opts out of that
/// guarantee.
/// </para>
/// <para>
/// <see cref="Scopes"/> defaults to <see cref="HttpInterceptorScopes.All"/> — always correct,
/// never fastest. An implementation that participates in only one phase should narrow it
/// (<see cref="HttpInterceptorScopes.Request"/> or <see cref="HttpInterceptorScopes.Response"/>)
/// so the transport skips it entirely on the other phase and its zero-cost fast paths are
/// preserved: a request-only interceptor must never be the reason a response sink and exchange
/// control are constructed.
/// </para>
/// <para>
/// The per-hook execution constraints are part of the inherited contract (see
/// <see cref="IHttpExchangeInterceptor"/>): the <see langword="void"/> parse-path hooks must stay
/// CPU-only, the <see cref="ValueTask"/> send-path hooks may await, instances are shared across
/// all connections and requests and must be stateless.
/// </para>
/// </remarks>
public abstract class HttpExchangeInterceptor : IHttpExchangeInterceptor
{
    /// <inheritdoc />
    public virtual HttpInterceptorScopes Scopes => HttpInterceptorScopes.All;

    /// <inheritdoc />
    public virtual void AfterRequestHead(HttpRequestInterceptorContext context)
    {
    }

    /// <inheritdoc />
    public virtual void BeforeRequestBody(HttpRequestInterceptorContext context)
    {
    }

    /// <inheritdoc />
    public virtual Stream AfterRequestBody(HttpRequestInterceptorContext context, Stream body)
    {
        return body;
    }

    /// <inheritdoc />
    public virtual void BeforeResponse(HttpResponseInterceptorContext context)
    {
    }

    /// <inheritdoc />
    public virtual ValueTask BeforeResponseHeadAsync(HttpResponseInterceptorContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public virtual ValueTask AfterResponseAsync(HttpResponseInterceptorContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}
