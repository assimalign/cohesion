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
/// This feature carries only what the terminal runs. The endpoint's model (its route, values and
/// metadata) stays in the package that selected it.
/// </para>
/// </remarks>
public interface IWebEndpointFeature : IHttpFeature
{
    /// <summary>
    /// Gets the delegate the pipeline's terminal runs for the selected endpoint.
    /// </summary>
    WebApplicationMiddleware Endpoint { get; }
}
