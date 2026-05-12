using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// 
/// </summary>
public interface IApplicationBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="resource"></param>
    /// <returns></returns>
    IApplicationBuilder AddResource(IApplicationResource resource);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IApplicationBuilder AddResource(Func<IApplicationModel, IApplicationResource> configure);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IApplication Build();
}
