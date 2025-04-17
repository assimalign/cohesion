
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

using Hosting;

/// <summary>
/// 
/// </summary>
public interface ICohesionApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    ICohesionApplicationBuilder ConfigureApplication(Func<ICohesionApplicationBuilder, IApplication> configure);
}
