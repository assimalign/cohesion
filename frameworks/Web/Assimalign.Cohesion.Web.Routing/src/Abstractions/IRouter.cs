using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Routing;

using Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IRouter
{
    /// <summary>
    /// Gets the routes associated with the router.
    /// </summary>
    IEnumerable<IRouterRoute> Routes { get; }

    /// <summary>
    /// Rou
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RouteAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
