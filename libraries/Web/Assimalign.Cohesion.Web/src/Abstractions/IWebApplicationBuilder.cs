using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="server"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddServer(IWebApplicationServer server);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IWebApplication Build();
}
