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
using Assimalign.Cohesion.Http;

public sealed class WebApplicationBuilder : IWebApplicationBuilder, IHostBuilder
{
    private readonly WebApplicationOptions _options;
    private readonly WebApplicationContext _context;

    public WebApplicationBuilder(WebApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Environment = new HostEnvironment(options.Environment!);
        Configuration = new ConfigurationManager();
        Logging = new LoggerFactoryBuilder();
        Services = new ServiceProviderBuilder();
        ServerManager = new WebApplicationServerManager(this);

        _options = options;
        _context = new WebApplicationContext(Services);
    }

    /// <summary>
    /// 
    /// </summary>
    public HostEnvironment Environment { get; }

    /// <summary>
    /// 
    /// </summary>
    public WebApplicationServerManager ServerManager { get; } 

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
    public LoggerFactoryBuilder Logging { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public WebApplication Build()
    {
        WebApplication app = new WebApplication(_context, _options);

        Services.AddSingleton<WebApplicationServer>();
        Services.AddSingleton<IHostService>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService <Internal.WebApplicationServer>();
        });
        Services.AddSingleton<IHostEnvironment>(Environment);
        Services.AddSingleton<IConfiguration>(Configuration);
        Services.AddSingleton<ILoggerFactory>(Logging.Build());
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
    IHost IHostBuilder.Build()
    {
        return Build();
    }
    //IWebApplicationBuilder IWebApplicationBuilder.UseServer(IWebApplicationServer server)
    //{
    //    ServerManager.UseServer(server);
    //    return this;
    //}
    //IHostBuilder IHostBuilder.AddHostedService(IHostService service)
    //{
    //    ArgumentNullException.ThrowIfNull(service);

    //    Services.AddSingleton(service);

    //    return this;
    //}
    //IHostBuilder IHostBuilder.AddHostedService(Func<IHostContext, IHostService> configure)
    //{
    //    Services.AddSingleton(_ => configure.Invoke(_context));
    //    return this;
    //}

    IWebApplicationBuilder IWebApplicationBuilder.AddFeature(IHttpFeature feature)
    {
        Services.AddSingleton<IHttpFeature>(feature);
        return this;
    }
}
