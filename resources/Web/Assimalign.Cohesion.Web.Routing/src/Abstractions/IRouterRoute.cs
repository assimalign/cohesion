using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

public interface IRouterRoute
{
    /// <summary>
    /// A handler mapped to the route, which will be executed when the route is successfully matched.
    /// </summary>
    IRouterRouteHandler Handler { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="method"></param>
    /// <param name="path"></param>
    /// <param name="values"></param>
    /// <returns></returns>
    bool TryMatch(IHttpContext context, out RouteValueDictionary values);
}
