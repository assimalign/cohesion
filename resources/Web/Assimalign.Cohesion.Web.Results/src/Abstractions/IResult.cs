using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A deferred HTTP response: a value a handler <em>returns</em> that knows how to write itself onto
/// the exchange when the pipeline executes it. This is the general result abstraction that
/// endpoints, controllers, and source-generated bindings produce instead of writing to
/// <see cref="IHttpResponse"/> imperatively.
/// </summary>
/// <remarks>
/// <para>
/// A result executes against the bare exchange — <see cref="IHttpContext.Response"/> plus the typed
/// features on <see cref="IHttpContext.Features"/> — mirroring the handler idiom
/// (<c>IRouterRouteHandler.InvokeAsync</c> / <c>WebApplicationMiddleware</c>). There is no
/// request-time service location: <see cref="IHttpContext"/> exposes no service provider, so
/// everything a result needs is captured at construction time. Composition (which results exist,
/// what they carry) is decided at builder time; execution is a pure write against the exchange.
/// </para>
/// <para>
/// Built-in results are created through the <see cref="Results"/> factory (returns
/// <see cref="IResult"/>) or the <see cref="TypedResults"/> factory (returns the concrete carrier
/// types for return-type inference and endpoint metadata). Implementations must be safe to execute
/// exactly once per instance; the built-ins carry only immutable state and may be reused across
/// exchanges.
/// </para>
/// </remarks>
public interface IResult
{
    /// <summary>
    /// Writes this result to the exchange's response: status code, headers, and body as the
    /// concrete result defines them.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the response write.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
