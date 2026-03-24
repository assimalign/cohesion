using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Routing;

using Assimalign.Cohesion.Http;

/// <summary>
/// A factory based approach for creating middleware handlers for the router. This allows for more flexibility and 
/// separation of concerns, as the router can delegate the responsibility of creating middleware handlers to a separate component. 
/// The IRouterMiddlewareHandler interface defines a single method, Create, which takes an IHttpContext and returns a WebApplicationMiddleware instance. 
/// This allows for different implementations of the IRouterMiddlewareHandler interface to create different types of middleware 
/// handlers based on the context of the request.
/// </summary>
public interface IRouterRouteHandler
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
