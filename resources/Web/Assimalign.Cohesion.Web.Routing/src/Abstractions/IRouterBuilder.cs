namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// 
/// </summary>
public interface IRouterBuilder
{
    /// <summary>
    /// Adds the specified route to the router builder.
    /// </summary>
    /// <param name="route">The route to add to the router builder. Cannot be null.</param>
    /// <returns>The current instance of the router builder with the specified route added.</returns>
    IRouterBuilder Map(IRouterRoute route);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IRouter Build();
}
