using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The endpoint selected for the current exchange. The application pipeline's terminal runs it once
/// every middleware has called <c>next</c>.
/// </summary>
/// <remarks>
/// <para>
/// Selecting an endpoint and running it are separate pipeline steps. A selecting middleware, such as
/// <c>UseRouting</c> in <c>Assimalign.Cohesion.Web.Routing</c>, publishes this feature and calls
/// <c>next</c>, so every middleware registered after it runs with the endpoint known and can read the
/// endpoint's metadata through the selecting package's own contract. The pipeline's terminal then runs
/// <see cref="Endpoint"/>. A middleware that short-circuits (does not call <c>next</c>) prevents the
/// endpoint from running.
/// </para>
/// <para>
/// When no endpoint is selected, this feature is absent and the terminal ends the exchange as
/// unhandled. A pipeline builder's terminal must honor this contract: run the published endpoint when
/// present, and fall back to its unhandled-request behavior otherwise.
/// </para>
/// <para>
/// This feature carries what the terminal runs and the template telemetry names the endpoint by
/// (<see cref="RouteTemplate"/>). The endpoint's model (its route, values and metadata) stays in the
/// package that selected it.
/// </para>
/// </remarks>
public interface IWebEndpointFeature : IHttpFeature
{
    /// <summary>
    /// Gets the delegate the pipeline's terminal runs for the selected endpoint.
    /// </summary>
    WebApplicationMiddleware Endpoint { get; }

    /// <summary>
    /// Gets the route template the endpoint was selected by, as telemetry reports it, or
    /// <see langword="null"/> when the endpoint has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The template is the low-cardinality name of the endpoint, for example <c>/orders/{id}</c>: the
    /// default server reports it as the OpenTelemetry <c>http.route</c> attribute of the request's span
    /// and duration metric, and names the span <c>GET /orders/{id}</c>. It must never be the request
    /// path, whose cardinality is unbounded. <c>UseRouting</c> publishes the matched route's template with
    /// a single leading <c>/</c>; its <c>405</c> endpoint, which no single route selected, has none.
    /// </para>
    /// <para>
    /// Implemented as a default interface member that returns <see langword="null"/>, so an endpoint
    /// selector that has no template needs no change.
    /// </para>
    /// </remarks>
    string? RouteTemplate => null;
}
