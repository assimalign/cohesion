
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
    /// <typeparam name="TFeature"></typeparam>
    /// <param name="feature"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddFeature<TFeature>(TFeature feature);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IWebApplication Build();
}
