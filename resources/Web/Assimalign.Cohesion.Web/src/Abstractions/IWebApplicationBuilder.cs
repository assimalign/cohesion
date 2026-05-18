
using System;

namespace Assimalign.Cohesion.Web;

using Http;


/// <summary>
/// 
/// </summary>
public interface IWebApplicationBuilder 
{
    /// <summary>
    /// Adds a feature to be used within the HttpContext Feature Collection. <see cref="IHttpContext.Features"/>
    /// </summary>
    /// <param name="feature"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddFeature(IHttpFeature feature);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IWebApplication Build();
}
