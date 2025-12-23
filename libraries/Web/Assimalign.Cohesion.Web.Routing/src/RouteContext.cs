using System;

namespace Assimalign.Cohesion.Web.Routing;

using Assimalign.Cohesion.Http;

public class RouteContext
{
    private RouteData _routeData = new RouteData();

    /// <summary>
    /// Creates a new instance of <see cref="RouteContext"/> for the provided <paramref name="httpContext"/>.
    /// </summary>
    /// <param name="httpContext">The <see cref="Http.HttpContext"/> associated with the current request.</param>
    public RouteContext(IHttpContext httpContext)
    {
        HttpContext = ArgumentNullException.ThrowIfNull<IHttpContext>(httpContext);
    }

    /// <summary>
    /// Gets or sets the handler for the request. An <see cref="IRouter"/> should set <see cref="Handler"/>
    /// when it matches.
    /// </summary>
    public IHttpApplication? HttpApplication { get; set; }

    /// <summary>
    /// Gets the <see cref="Http.HttpContext"/> associated with the current request.
    /// </summary>
    public IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets or sets the <see cref="Routing.RouteData"/> associated with the current context.
    /// </summary>
    public RouteData RouteData
    {
        get
        {
            return _routeData;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _routeData = value;
        }
    }
}
