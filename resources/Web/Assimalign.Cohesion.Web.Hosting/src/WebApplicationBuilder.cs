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
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting;

/// <summary>
/// Composes a Web application. Every dependency the application runs with is a registration in
/// <see cref="Services"/>: lifecycle services (<see cref="IHostService"/>), servers
/// (<see cref="IWebApplicationServer"/>), request features (<see cref="IHttpFeature"/>), and
/// health contributions (<see cref="IHealthContributor"/>).
/// </summary>
/// <remarks>
/// The root <see cref="IWebApplicationBuilder"/> verbs are implemented explicitly as shims over
/// those registrations, so feature libraries compose against the dependency-free root contract
/// while this module owns the container. <see cref="Build"/> closes registration, creates the
/// service provider once, and resolves the lifecycle services; servers and features are resolved
/// once at their own composition boundary (host start and pipeline build). Nothing is resolved
/// per request.
/// </remarks>
public sealed class WebApplicationBuilder : IWebApplicationBuilder, IHostBuilder
{
    private readonly WebApplicationOptions _options;
    private readonly WebApplicationContext _context;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;

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
            _resourceContext = resourceContext;
            options.Environment = resourceContext.EnvironmentName;
        }

        string contentRootPath = ResolveContentRoot(options.ContentRootPath, resourceContext);
        Environment = new HostEnvironment(options.Environment!)
        {
            ContentRootPath = FileSystemPath.Parse(contentRootPath),
        };
        Configuration = new ConfigurationManager();
        if (args is not null)
        {
            AddDefaultConfiguration(args, contentRootPath, resourceContext);
        }

        Logging = new LoggerFactoryBuilder();
        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        Server = new WebApplicationServerBuilder(this);

        _context = new WebApplicationContext
        {
            ContentRootPath = Environment.ContentRootPath,
            WebRootPath = ResolveWebRoot(contentRootPath, options.WebRootPath),
        };

        if (_controlPlane is not null && resourceContext is not null)
        {
            if (ResourceTelemetry.Configure(resourceContext, Logging, out IHostService? telemetry))
            {
                // Registered ahead of every AddService call, so telemetry starts first and stops last.
                Services.AddSingleton<IHostService>(telemetry!);
            }
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
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when <see cref="Build"/> runs; a later registration throws
    /// <see cref="InvalidOperationException"/>. Register factory or instance services: the
    /// provider is created without dynamic code. A factory-created service is owned by the
    /// application and disposed with it; an instance stays owned by its caller.
    /// </remarks>
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

        Services.AddSingleton<IHealthContributor>(new DelegateHealthContributor(name, check));
        return this;
    }

    /// <summary>
    /// Adds a lifecycle service to the application.
    /// </summary>
    /// <remarks>
    /// Lifecycle services start in registration order before every Web server and stop in
    /// reverse order after every Web server has stopped. The service is registered in
    /// <see cref="Services"/> as an <see cref="IHostService"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The lifecycle service to add.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public WebApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Adds a lifecycle service created from the final concrete application context.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once when the application is built, and the application owns and
    /// disposes the service it returns. Lifecycle services start in registration order before
    /// every Web server and stop in reverse order after every Web server has stopped.
    /// </remarks>
    /// <param name="factory">The factory that creates the lifecycle service.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The factory returns <see langword="null"/> when the application is built.
    /// </exception>
    public WebApplicationBuilder AddService(Func<WebApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory.Invoke(_context)
            ?? throw new InvalidOperationException("The web application service factory returned null."));
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

        if (_controlPlane is not null && _resourceContext?.GatewayName is not null &&
            (_controlPlane.ObservedEndpoints.ContainsKey("http") || _controlPlane.ObservedEndpoints.ContainsKey("https")))
        {
            ResourceControlPlaneMiddleware.Validate(_resourceContext);
        }

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
            IWebApplicationPipeline pipeline = _pipeline is null
                ? serviceProvider.GetRequiredService<IWebApplicationPipelineBuilder>().Build()
                : new BorrowedWebApplicationPipeline(_pipeline);

            if (_controlPlane is null)
            {
                return pipeline;
            }
            int? port = (_controlPlane.ObservedEndpoints.TryGetValue("http", out Uri? endpoint) ||
                _controlPlane.ObservedEndpoints.TryGetValue("https", out endpoint)) ? endpoint.Port : null;
            return new EnabledResourcePipeline(_controlPlane, _resourceContext!, _context, port, pipeline);
        });

        _isBuilt = true;

        // Registration closes here. The provider copies the registrations, so one added after
        // Build would silently never reach it; the read-only container turns that into an error.
        if (Services.Container is ServiceContainer container)
        {
            container.MakeReadOnly();
        }

        _context.SetServiceProvider(((IServiceProviderBuilder)Services).Build());
        try
        {
            // Materializes the lifecycle services once, in registration order, against the final
            // context. Servers resolve later, at host start, after the pipeline is composed.
            _context.ResolveApplicationServices();

            if (_controlPlane is not null)
            {
                foreach (IHealthContributor contributor in
                    _context.ServiceProvider.GetRequiredService<IEnumerable<IHealthContributor>>())
                {
                    _controlPlane.AddHealthContributor(contributor);
                }

                ResourceRuntime.HostBuilt(app, _controlPlane);
            }
        }
        catch (Exception exception)
        {
            // The provider owns every service a factory created before the failure.
            try
            {
                Task.Run(() => _context.DisposeServiceProviderAsync().AsTask()).GetAwaiter().GetResult();
            }
            catch (Exception disposalException)
            {
                throw new AggregateException(exception, disposalException);
            }

            throw;
        }

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

    // The explicit option wins, then the ambient resource context, then the application's base
    // directory (where the SDK copies appsettings*.json and wwwroot).
    private static string ResolveContentRoot(FileSystemPath? configured, ResourceContext? resourceContext)
    {
        if (configured is { IsEmpty: false } contentRoot)
        {
            return System.IO.Path.GetFullPath(contentRoot.ToString());
        }

        return resourceContext?.ContentRootPath ?? AppContext.BaseDirectory;
    }

    // A configured web root is resolved against the content root and kept even when it does not
    // exist yet (the static-files middleware then serves nothing). Without one, wwwroot under the
    // content root is the web root only when that directory exists. The content root itself is
    // never a web root: it holds appsettings*.json and the application's binaries.
    private static FileSystemPath? ResolveWebRoot(string contentRootPath, FileSystemPath? configured)
    {
        if (configured is { IsEmpty: false } webRoot)
        {
            string path = webRoot.ToString();
            return FileSystemPath.Parse(System.IO.Path.IsPathRooted(path)
                ? System.IO.Path.GetFullPath(path)
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(contentRootPath, path)));
        }

        // Explicit branches: FileSystemPath converts implicitly from string, so "cond ? path : null"
        // would type as FileSystemPath and turn the null into an empty (non-null) path.
        string defaultWebRoot = System.IO.Path.Combine(contentRootPath, "wwwroot");
        if (!Directory.Exists(defaultWebRoot))
        {
            return null;
        }

        return FileSystemPath.Parse(defaultWebRoot);
    }

    private void AddDefaultConfiguration(string[] args, string contentRootPath, ResourceContext? resourceContext)
    {
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
    IWebApplicationBuilder IWebApplicationBuilder.AddServer(IWebApplicationServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        Services.AddSingleton<IWebApplicationServer>(server);
        return this;
    }

    IWebApplicationBuilder IWebApplicationBuilder.AddServer(Func<IWebApplicationContext, IWebApplicationServer> server)
    {
        ArgumentNullException.ThrowIfNull(server);

        Services.AddSingleton<IWebApplicationServer>(_ => server.Invoke(_context)
            ?? throw new InvalidOperationException("The web application server factory returned null."));
        return this;
    }

    IWebApplicationBuilder IWebApplicationBuilder.AddPipeline(IWebApplicationPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        // A single replacement for the default pipeline, not a registration: the hosted
        // IWebApplicationPipeline registered at Build wraps it with the resource terminals.
        _pipeline = pipeline;
        return this;
    }

    IWebApplicationBuilder IWebApplicationBuilder.AddFeature(IHttpFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        Services.AddSingleton<IHttpFeature>(feature);
        return this;
    }

    IWebApplicationBuilder IWebApplicationBuilder.AddFeature(Func<IWebApplicationContext, IHttpFeature> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Services.AddSingleton<IHttpFeature>(_ => configure.Invoke(_context)
            ?? throw new InvalidOperationException("The web application feature factory returned null."));
        return this;
    }
}
