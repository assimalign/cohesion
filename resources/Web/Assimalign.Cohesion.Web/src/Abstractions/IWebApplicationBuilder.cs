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
    /// <typeparam name="TFeature"></typeparam>
    /// <param name="feature"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddFeature<TFeature>(TFeature feature) where TFeature : IHttpFeature;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddFeature(Func<IWebApplicationContext, IHttpFeature> configure);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="server"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddServer(IWebApplicationServer server);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pipeline"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddPipeline(IWebApplicationPipeline pipeline);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IWebApplication Build();
}
