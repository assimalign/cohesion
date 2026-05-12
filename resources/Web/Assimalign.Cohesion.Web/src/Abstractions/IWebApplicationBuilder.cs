
using System;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public interface IWebApplicationBuilder 
{
    /// <summary>
    /// 
    /// </summary>
    IHostEnvironment Environment { get; }

    /// <summary>
    /// 
    /// </summary>
    IServiceProviderBuilder Services { get; }

    /// <summary>
    /// 
    /// </summary>
    IConfigurationManager Configuration { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IWebApplication Build();
}
