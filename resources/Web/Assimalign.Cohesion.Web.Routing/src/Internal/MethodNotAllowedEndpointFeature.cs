using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// The endpoint <c>UseRouting</c> publishes when a route matched the request path but not its method:
/// the pipeline's terminal answers <c>405 Method Not Allowed</c> with the <c>Allow</c> header
/// (RFC 9110 §15.5.6). It is not an <see cref="IRouteMatchFeature"/>, because no route was selected,
/// so endpoint-metadata consumers see no metadata for the exchange.
/// </summary>
internal sealed class MethodNotAllowedEndpointFeature : IWebEndpointFeature
{
    private readonly RouteMatch _match;

    /// <summary>
    /// Initializes a new 405 endpoint for a <see cref="RouteMatchStatus.MethodNotAllowed"/> match.
    /// </summary>
    /// <param name="match">The match whose acceptable methods form the <c>Allow</c> header.</param>
    public MethodNotAllowedEndpointFeature(RouteMatch match)
    {
        _match = match;
        Endpoint = WriteAsync;
    }

    /// <inheritdoc />
    public string Name => nameof(IWebEndpointFeature);

    /// <inheritdoc />
    public WebApplicationMiddleware Endpoint { get; }

    private Task WriteAsync(IHttpContext context)
    {
        Router.ApplyMethodNotAllowed(context, _match);
        return Task.CompletedTask;
    }
}
