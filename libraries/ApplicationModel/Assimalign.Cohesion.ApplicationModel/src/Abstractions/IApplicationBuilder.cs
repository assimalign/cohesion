using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

using Hosting;
using Logging;
using Configuration;
using DependencyInjection;

/// <summary>
/// 
/// </summary>
public interface IApplicationBuilder<TContext> : IHostBuilder where TContext : IApplicationContext
{
    /// <summary>
    /// 
    /// </summary>
    ILoggerFactoryBuilder Logging { get; }

    /// <summary>
    /// 
    /// </summary>
    IConfigurationManager Configuration { get; }

    /// <summary>
    /// 
    /// </summary>
    IServiceProviderBuilder Services { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    new IApplication<TContext> Build();
}
