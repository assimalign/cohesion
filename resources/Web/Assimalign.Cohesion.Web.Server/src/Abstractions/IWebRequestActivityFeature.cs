using System.Diagnostics;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Exposes the server activity owned by the current HTTP exchange.
/// </summary>
/// <remarks>
/// The server owns the activity's lifetime. Consumers may use it for correlation and enrichment,
/// but must not stop or dispose it. Unlike <see cref="System.Diagnostics.Activity.Current"/>, this
/// feature never returns an unrelated ambient activity or a child activity created by middleware.
/// A custom <see cref="IWebApplicationServer"/> may omit the feature.
/// </remarks>
public interface IWebRequestActivityFeature : IHttpFeature
{
    /// <summary>
    /// Gets this exchange's server activity, or <see langword="null"/> when no activity was created,
    /// for example because there was no listener or the sampler declined the exchange.
    /// </summary>
    Activity? Activity { get; }
}
