using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Web.Internal;

public sealed class WebApplicationBuilder : IWebApplicationBuilder
{

    #region Constructors

    public WebApplicationBuilder()
    {
        Services = new ServiceProviderBuilder();
        Configuration = new ConfigurationManager();
    }

    public WebApplicationBuilder(HostBuilder host) : this()
    {
        Host = host;
    }

    #endregion

    #region Properties

    /// <summary>
    /// 
    /// </summary>
    public HostBuilder Host { get; }

    /// <summary>
    /// 
    /// </summary>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// The root configuration provider.
    /// </summary>
    public ConfigurationManager Configuration { get; }

    #endregion

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public WebApplication Build()
    {
        Services.AddSingleton<IConfigurationRoot>(Configuration);
        Services.AddSingleton<IConfiguration>(Configuration);

        var context = new WebApplicationContext()
        {
            Services = ((IServiceProviderBuilder)Services).Build(),
            Configuration = Configuration
        };

        var app = new WebApplication(context);

        Host.AddService(app);

        return app;
    }


    IWebApplication IWebApplicationBuilder.Build()
    {
        return Build();
    }
}
