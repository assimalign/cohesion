using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

/// <summary>Handles one matched control-plane route.</summary>
/// <param name="context">The request being served.</param>
/// <param name="routeValues">
/// The template parameter values captured from the request path, keyed case-insensitively by
/// parameter name; empty for a template without parameters.
/// </param>
/// <param name="cancellationToken">Cancels request handling.</param>
/// <returns>A task that completes when the response is prepared.</returns>
internal delegate Task ControlPlaneRouteHandler(
    IHttpContext context,
    IReadOnlyDictionary<string, string> routeValues,
    CancellationToken cancellationToken);
