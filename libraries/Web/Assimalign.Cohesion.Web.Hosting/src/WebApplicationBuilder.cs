using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Internal;

public sealed class WebApplicationBuilder : IWebApplicationBuilder
{
    private readonly WebApplicationOptions _options;
    private readonly WebApplicationContext _context;

    public WebApplicationBuilder(WebApplicationOptions options)
    {
        Environment = new HostEnvironment(options.Environment!);
        Configuration = new ConfigurationManager();
        Services = new ServiceProviderBuilder();
        Web = new WebApplicationServerBuilder(this);

        _options = ThrowHelper.ThrowIfNull(options);
        _context = new WebApplicationContext(Services);
    }

    /// <summary>
    /// 
    /// </summary>
    public HostEnvironment Environment { get; }

    /// <summary>
    /// 
    /// </summary>
    public WebApplicationServerBuilder Web { get; } 

    /// <summary>
    /// 
    /// </summary>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// 
    /// </summary>
    public ConfigurationManager Configuration { get; }

    

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public WebApplication Build()
    {
        var app = new WebApplication(_context, _options);

        Services.AddSingleton<IHostEnvironment>(Environment);
        Services.AddSingleton<IConfigurationRoot>(Configuration);
        Services.AddSingleton<IConfiguration>(Configuration);
        Services.AddSingleton<IWebApplicationPipelineBuilder>(app);
        Services.AddSingleton<IWebApplicationPipeline>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<IWebApplicationPipelineBuilder>().Build();
        });

        return app;
    }

    IWebApplication IWebApplicationBuilder.Build()
    {
        return Build();
    }
    IWebApplicationBuilder IWebApplicationBuilder.AddServer(IWebApplicationServer server)
    {
        throw new NotImplementedException();
    }



}
