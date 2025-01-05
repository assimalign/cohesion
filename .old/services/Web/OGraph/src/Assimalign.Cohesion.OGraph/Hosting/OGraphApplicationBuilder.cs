

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.OGraph;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Configuration;

public sealed class OGraphApplicationBuilder
{
    internal OGraphApplicationBuilder()
    {
        Services = new ServiceProviderBuilder();
        Configuration = new ConfigurationBuilder();
    }

    /// <summary>
    /// 
    /// </summary>
    public IConfigurationBuilder Configuration { get; init; }
    /// <summary>
    /// 
    /// </summary>
    public IServiceProviderBuilder Services { get; init; }
    
}
