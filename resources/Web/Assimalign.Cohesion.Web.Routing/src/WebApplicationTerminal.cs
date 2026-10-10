using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// The standard terminal of a Web application pipeline: the innermost delegate a pipeline builder
/// composes, reached when every middleware called <c>next</c>.
/// </summary>
/// <remarks>
/// <para>
/// The terminal runs the endpoint a selecting middleware published (<see cref="IWebEndpointFeature"/>,
/// for example a route <c>UseRouting</c> matched). With no endpoint selected the request went unhandled,
/// and the terminal sets a bodyless <c>404 Not Found</c> rather than completing silently, which would
/// hand the transport an empty <c>200</c>. A response a middleware already shaped is left untouched: a
/// non-<c>200</c> status, a <c>Location</c> header, a <c>Content-Type</c>, or a written body.
/// </para>
/// <para>
/// The application pipeline in <c>Web.Hosting</c> and every non-rejoining branch
/// (<c>Map(path, branch)</c>, <c>MapWhen</c>) end in this terminal, so an endpoint selected before a branch
/// still runs inside it. A custom <see cref="IWebApplicationPipelineBuilder"/> should end in it too.
/// The 404 carries no payload, because routing references no error-handling library.
/// <c>UseStatusCodePages</c> in <c>Web.ErrorHandling</c> upgrades it to RFC 9457 problem+json.
/// </para>
/// </remarks>
public static class WebApplicationTerminal
{
    /// <summary>
    /// Runs the exchange's selected endpoint, or ends an unhandled exchange with a bodyless
    /// <c>404 Not Found</c>.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <returns>A task that completes when the endpoint (or the 404) has completed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static Task InvokeAsync(IHttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Features.Get<IWebEndpointFeature>() is { } endpoint)
        {
            return endpoint.Endpoint.Invoke(context);
        }

        IHttpResponse response = context.Response;

        if (response.StatusCode.Value == HttpStatusCode.Ok.Value &&
            !response.Headers.ContainsKey(HttpHeaderKey.Location) &&
            !response.Headers.ContainsKey(HttpHeaderKey.ContentType) &&
            !(response.Body.CanSeek && response.Body.Length > 0))
        {
            response.StatusCode = HttpStatusCode.NotFound;
        }

        return Task.CompletedTask;
    }
}
