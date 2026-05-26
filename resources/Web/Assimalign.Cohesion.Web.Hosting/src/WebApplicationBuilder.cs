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

    private bool _isBuilt;

    public WebApplicationBuilder(WebApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Environment = new HostEnvironment(options.Environment!);
        Configuration = new ConfigurationManager();
        Logging = new LoggerFactoryBuilder();
        Services = new ServiceProviderBuilder();
        Server = new WebApplicationServerBuilder(this);

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
    public WebApplicationServerBuilder Server { get; } 

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
        InvalidOperationException.ThrowIf(_isBuilt, "The application has already been built.");

        WebApplication app = new WebApplication(_context, _options);

        Services.AddSingleton<IHostEnvironment>(Environment);
        Services.AddSingleton<IConfiguration>(Configuration);
        Services.AddSingleton<ILoggerFactory>(Logging.Build());
        Services.AddSingleton<IWebApplicationContext>(_context);
        Services.AddSingleton<IWebApplicationPipelineBuilder>(app);
        Services.AddSingleton<IWebApplicationPipeline>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<IWebApplicationPipelineBuilder>().Build();
        });

        _isBuilt = true;

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

    

    IWebApplicationBuilder IWebApplicationBuilder.AddServer(IWebApplicationServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Server.UseServer(server);
        return this;
    }

    IWebApplicationBuilder IWebApplicationBuilder.AddPipeline(IWebApplicationPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        // Remove the default pipeline builder and replace it with the provided pipeline
        Services.RemoveAll<IWebApplicationPipelineBuilder>();

        // The user is override the default pipeline, so we need to register the provided 
        // pipeline as the implementation of IWebApplicationPipeline
        Services.AddSingleton<IWebApplicationPipeline>(pipeline);
        return this;
    }

    IWebApplicationBuilder IWebApplicationBuilder.AddFeature(IHttpFeature feature)
    {
        return ((IWebApplicationBuilder)this).AddFeature(_ => feature);
    }
    IWebApplicationBuilder IWebApplicationBuilder.AddFeature<TFeature>(TFeature feature)
    {
        return ((IWebApplicationBuilder)this).AddFeature((IHttpFeature)feature);
    }
    IWebApplicationBuilder IWebApplicationBuilder.AddFeature(Func<IWebApplicationContext, IHttpFeature> configure)
    {
        Services.AddSingleton<IHttpFeature>(configure.Invoke(_context));
        return this;
    }
}
