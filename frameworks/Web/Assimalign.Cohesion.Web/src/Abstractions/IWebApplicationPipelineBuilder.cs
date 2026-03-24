using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationPipelineBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IWebApplicationPipeline Build();
}
