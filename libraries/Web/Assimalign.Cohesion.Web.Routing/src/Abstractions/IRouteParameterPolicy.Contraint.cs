using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing;

using Cohesion.Http;


public interface IRouteParameterConstraintPolicy : IRouteParameterPolicy
{
    bool Match(
        IHttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection);
}
