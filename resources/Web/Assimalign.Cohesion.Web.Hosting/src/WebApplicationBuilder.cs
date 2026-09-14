using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.CommandLine;
using Assimalign.Cohesion.Configuration.Json;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Hosting.Internal;

namespace Assimalign.Cohesion.Web.Hosting;

public sealed class WebApplicationBuilder : IWebApplicationBuilder, IHostBuilder
{
    private readonly WebApplicationOptions _options;
    private readonly WebApplicationContext _context;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ReadOnlyMemory<byte> _bootstrapCredential;
    private readonly bool _requireControlPlaneAuthentication;
    private readonly List<IHealthContributor> _healthContributors = new();
    private readonly List<Func<IWebApplicationContext, IHostService>> _serviceRegistrations = new();

    private IWebApplicationPipeline? _pipeline;

    private bool _isBuilt;

    internal void OwnEndpointCertificate(X509Certificate2 certificate) => _context.EndpointCertificates.Add(certificate);

    public WebApplicationBuilder(WebApplicationOptions options)
        : this(options, resourceAssembly: null)
    {
    }

    internal WebApplicationBuilder(WebApplicationOptions options, Assembly? resourceAssembly)
        : this(options, resourceAssembly, args: null)
    {
    }

    internal WebApplicationBuilder(
        WebApplicationOptions options,
        Assembly? resourceAssembly,
        string[]? args)
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
            _bootstrapCredential = resourceContext.BootstrapCredential.ToArray();
            _requireControlPlaneAuthentication = resourceContext.GatewayName is not null;
            options.Environment = resourceContext.EnvironmentName;
        }

        Environment = resourceContext is null
            ? new HostEnvironment(options.Environment!)
            : new HostEnvironment(options.Environment!)
            {
                ContentRootPath = FileSystemPath.Parse(resourceContext.ContentRootPath),
            };
        Configuration = new ConfigurationManager();
        if (args is not null)
        {
            AddDefaultConfiguration(args, resourceContext);
        }

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

    /// <inheritdoc cref="IWebApplicationBuilder.AddService(IHostService)" />
    public WebApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    /// <inheritdoc cref="IWebApplicationBuilder.AddService(Func{IWebApplicationContext, IHostService})" />
    public WebApplicationBuilder AddService(Func<IWebApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public WebApplication Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The application has already been built.");
        InvalidOperationException.ThrowIf(
            _options.StartServicesConcurrently || _options.StopServicesConcurrently,
            "Web application servers require serial host lifecycle execution so they start in registration order and stop in reverse order.");

        var applicationOptions = new WebApplicationOptions
        {
            Environment = _options.Environment,
            ShutdownTimeout = _options.ShutdownTimeout,
            StartupTimeout = _options.StartupTimeout,
        };
        WebApplication app = new WebApplication(_context, applicationOptions);

        Services.AddSingleton<IHostEnvironment>(Environment);
        Services.AddSingleton<IConfiguration>(Configuration);
        Services.AddSingleton<ILoggerFactory>(Logging.Build());
        Services.AddSingleton<IWebApplicationContext>(_context);
        Services.AddSingleton<IWebApplicationPipelineBuilder>(app);
        Services.AddSingleton<IWebApplicationPipeline>(serviceProvider =>
        {
            IWebApplicationPipeline pipeline = _pipeline ??
                serviceProvider.GetRequiredService<IWebApplicationPipelineBuilder>().Build();

            return _controlPlane is null
                ? pipeline
                : new ResourceControlPlanePipeline(
                    _controlPlane,
                    _bootstrapCredential,
                    _requireControlPlaneAuthentication,
                    _context,
                    pipeline);
        });

        var applicationServices = new IHostService[_serviceRegistrations.Count];
        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            applicationServices[index] = _serviceRegistrations[index].Invoke(_context)
                ?? throw new InvalidOperationException(
                    "The web application service factory returned null.");
        }

        _context.SetApplicationServices(applicationServices);

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
        string endpointName = "http";
        if (!controlPlane.ObservedEndpoints.TryGetValue(endpointName, out Uri? endpoint) &&
            !resourceContext.Endpoints.TryGetValue(endpointName, out endpoint))
        {
            endpointName = "https";
            if (!controlPlane.ObservedEndpoints.TryGetValue(endpointName, out endpoint) &&
                !resourceContext.Endpoints.TryGetValue(endpointName, out endpoint))
            {
                return;
            }
        }

        controlPlane.ObserveEndpoint(endpointName, endpoint);

        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The ambient Web endpoint '{endpointName}' must use http or https, not '{endpoint.Scheme}'.");
        }

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        if (string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            if (!resourceContext.TryGetEndpointCertificate(endpointName, out X509Certificate2? leaf, out X509Certificate2Collection chain))
            {
                throw new InvalidOperationException($"The ambient Web https endpoint '{endpointName}' requires its certificate Secret mount (default 'tls').");
            }
            _context.EndpointCertificates.Add(leaf);
            foreach (X509Certificate2 issuer in chain)
            {
                _context.EndpointCertificates.Add(issuer);
            }
            SslStreamCertificateContext certificate = SslStreamCertificateContext.Create(leaf, chain, offline: true);
            Server.UseServer(options => options.UseHttp1s(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port),
                new TlsServerOptions { AuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificateContext = certificate } }));
        }
        else
        {
            Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));
        }
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

    private void AddDefaultConfiguration(string[] args, ResourceContext? resourceContext)
    {
        string contentRootPath = resourceContext?.ContentRootPath ?? AppContext.BaseDirectory;
        var contentRoot = new PhysicalFileSystem(new PhysicalFileSystemOptions
        {
            Root = contentRootPath,
            IsReadOnly = true,
        });

        Configuration
            .AddJsonFile(contentRoot, "appsettings.json", optional: true)
            .AddJsonFile(
                contentRoot,
                $"appsettings.{Environment.Name}.json",
                optional: true)
            .AddEnvironmentVariables("COHESION_CONFIG__")
            .AddCommandLine(PrependAmbientSettings(args, resourceContext));
    }

    private static string[] PrependAmbientSettings(
        string[] args,
        ResourceContext? resourceContext)
    {
        if (resourceContext is null || resourceContext.Settings.Count == 0)
        {
            return args;
        }

        var combinedArgs = new string[resourceContext.Settings.Count + args.Length];
        int index = 0;
        foreach ((string key, string value) in resourceContext.Settings)
        {
            combinedArgs[index++] = $"--{key}={value}";
        }

        args.CopyTo(combinedArgs, index);
        return combinedArgs;
    }

    IWebApplication IWebApplicationBuilder.Build()
    {
        return Build();
    }
    IHost IHostBuilder.Build()
    {
        return Build();
    }
    IWebApplicationBuilder IWebApplicationBuilder.AddService(IHostService service)
    {
        return AddService(service);
    }
    IWebApplicationBuilder IWebApplicationBuilder.AddService(Func<IWebApplicationContext, IHostService> factory)
    {
        return AddService(factory);
    }
    IWebApplicationBuilder IWebApplicationBuilder.AddServer(IWebApplicationServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Services.AddSingleton<IHostService>(new WebApplicationServerLifecycleAdapter(server));
        return this;
    }

    IWebApplicationBuilder IWebApplicationBuilder.AddPipeline(IWebApplicationPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
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
        ArgumentNullException.ThrowIfNull(server);

        Services.AddSingleton<IHostService>(_ =>
        {
            IWebApplicationServer applicationServer = server.Invoke(_context)
                ?? throw new InvalidOperationException(
                    "The web application server factory returned null.");

            return new WebApplicationServerLifecycleAdapter(applicationServer);
        });

        return this;
    }
}
