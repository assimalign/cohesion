namespace Assimalign.Cohesion.Web.Routing.Policies;

public abstract class RouteParameterPolicy
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public abstract bool Applies(RouteParameterPolicyContext context);
}
