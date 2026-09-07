using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Hosting.Internal;

namespace Assimalign.Cohesion.Web.Hosting;

public sealed class WebApplicationBuilder : IWebApplicationBuilder, IHostBuilder
{
    private readonly WebApplicationOptions _options;
    private readonly WebApplicationContext _context;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly List<IHealthContributor> _healthContributors = new();

    private bool _isBuilt;

    public WebApplicationBuilder(WebApplicationOptions options)
        : this(options, resourceAssembly: null)
    {
    }

    internal WebApplicationBuilder(WebApplicationOptions options, Assembly? resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        ResourceContext? resourceContext = null;
        if (resourceAssembly is not null &&
            ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered Web resource control-plane factory returned null.");
            resourceContext = ResourceRuntime.Current;
            options.Environment = resourceContext.EnvironmentName;
        }

        Environment = new HostEnvironment(options.Environment!);
        Configuration = new ConfigurationManager();
        Logging = new LoggerFactoryBuilder();
        Services = new ServiceProviderBuilder();
        Server = new WebApplicationServerBuilder(this);

        _context = new WebApplicationContext(Services);

        if (_controlPlane is not null && resourceContext is not null)
        {
            ResourceRuntime.RegisterConnectionFactoryResolver(CreateConnectionFactory);
            Services.AddSingleton(_controlPlane);
            BindAmbientHttpEndpoint(resourceContext, _controlPlane);
        }
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
    /// Gets the generated resource control plane, or null when this is a plain application.
    /// </summary>
    internal IResourceControlPlane? ControlPlane => _controlPlane;

    /// <summary>
    /// Adds a named contribution to the enabled resource's aggregate health, readiness,
    /// and liveness reports.
    /// </summary>
    /// <param name="name">The stable contribution name.</param>
    /// <param name="check">The health evaluator.</param>
    /// <returns>The same builder for chaining.</returns>
    public WebApplicationBuilder AddHealthCheck(string name, ResourceHealthCheck check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);

        _healthContributors.Add(new DelegateHealthContributor(name, check));
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public WebApplication Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The application has already been built.");

        WebApplication app = new WebApplication(_context, _options, _controlPlane);

        Services.AddSingleton<IHostEnvironment>(Environment);
        Services.AddSingleton<IConfiguration>(Configuration);
        Services.AddSingleton<ILoggerFactory>(Logging.Build());
        Services.AddSingleton<IWebApplicationContext>(_context);
        Services.AddSingleton<IWebApplicationPipelineBuilder>(app);
        Services.AddSingleton<IWebApplicationPipeline>(serviceProvider =>
        {
            return serviceProvider.GetRequiredService<IWebApplicationPipelineBuilder>().Build();
        });

        if (_controlPlane is not null)
        {
            foreach (IHealthContributor contributor in _healthContributors)
            {
                _controlPlane.AddHealthContributor(contributor);
            }

            foreach (IHealthContributor contributor in
                _context.ServiceProvider.GetRequiredService<IEnumerable<IHealthContributor>>())
            {
                _controlPlane.AddHealthContributor(contributor);
            }

            ResourceRuntime.HostBuilt(app, _controlPlane);
        }


        _isBuilt = true;

        return app;
    }

    private void BindAmbientHttpEndpoint(
        ResourceContext resourceContext,
        IResourceControlPlane controlPlane)
    {
        if (!controlPlane.ObservedEndpoints.TryGetValue("http", out EndpointAddress endpoint) &&
            !resourceContext.Endpoints.TryGetValue("http", out endpoint))
        {
            return;
        }

        controlPlane.ObserveEndpoint("http", endpoint);

        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The ambient Web endpoint 'http' must use the http scheme, not '{endpoint.Scheme}'.");
        }

        IPAddress address = ResolveBindAddress(endpoint.Host);
        Server.UseServer(options =>
            options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));
    }

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(host, out IPAddress? address)
            ? address
            : throw new InvalidOperationException(
                $"The ambient Web endpoint host '{host}' is not a bindable IP address.");
    }

    private static object? CreateConnectionFactory(string protocol)
    {
        return string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase)
            ? new TcpConnectionFactory()
            : null;
    }

    IWebApplication IWebApplicationBuilder.Build()
    {
        return Build();
    }
    IHost IHostBuilder.Build()
    {
        return Build();
    }
    IWebApplicationBuilder IWebApplicationBuilder.AddServer(IWebApplicationServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        //Server.UseServer(server);
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

    IWebApplicationBuilder IWebApplicationBuilder.AddFeature(Func<IWebApplicationContext, IHttpFeature> configure)
    {
        Services.AddSingleton<IHttpFeature>(configure.Invoke(_context));
        return this;
    }

    IWebApplicationBuilder IWebApplicationBuilder.AddServer(Func<IWebApplicationContext, IWebApplicationServer> server)
    {
        throw new NotImplementedException();
    }
}
