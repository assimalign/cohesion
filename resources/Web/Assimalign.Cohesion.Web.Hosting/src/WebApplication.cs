using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Hosting;

public sealed class WebApplication : Host<WebApplicationContext>, IWebApplication, IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware;
    private readonly WebApplicationContext _context;
    private readonly WebApplicationOptions _options;

    private bool _isBuilt;

    internal WebApplication(
        WebApplicationContext context,
        WebApplicationOptions options) : base(options)
    {
        _context = context;
        _options = options;
        _middleware = new List<Func<WebApplicationMiddleware, WebApplicationMiddleware>>();
    }

    public override WebApplicationContext Context => _context;

    /// <inheritdoc />
    protected override async ValueTask DisposeAsync(bool disposing)
    {
        await base.DisposeAsync(disposing).ConfigureAwait(false);
        if (disposing)
        {
            // The application owns its service provider: disposing it releases every service a
            // registered factory created, after the host has stopped them. Instance registrations
            // stay with their callers.
            await _context.DisposeServiceProviderAsync().ConfigureAwait(false);

            foreach (X509Certificate2 certificate in _context.EndpointCertificates)
            {
                certificate.Dispose();
            }
            _context.EndpointCertificates.Clear();
        }
    }

    /// <summary>
    /// Resolves the application's servers, and with them its request pipeline, before any
    /// lifecycle service starts.
    /// </summary>
    /// <remarks>
    /// Resolving the servers builds the request pipeline the default server captures, which runs
    /// every middleware factory; routing builds its route table there. Doing it ahead of every
    /// service start makes a composition failure, such as an invalid route table, fail the start
    /// with nothing started and nothing to roll back, and leaves the host
    /// <see cref="HostState.Failed"/>.
    /// </remarks>
    /// <param name="cancellationToken">Aborts the startup if signaled.</param>
    /// <returns>A task that completes when the hook has finished.</returns>
    protected override Task OnStartingAsync(CancellationToken cancellationToken = default)
    {
        _ = _context.Servers;

        return base.OnStartingAsync(cancellationToken);
    }

    public WebApplication Use(Func<IHttpContext, WebApplicationMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        Func<IHttpContext, WebApplicationMiddleware, Task> middleware2 = middleware;
        ((IWebApplicationPipelineBuilder)this).Use((WebApplicationMiddleware next) => (IHttpContext context) =>
        {
            return middleware2.Invoke(context, next);
        });
        return this;
    }
    IWebApplicationContext IWebApplication.Context => Context;
    IWebApplicationPipeline IWebApplicationPipelineBuilder.Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The web host is already built.");

        var middleware = new WebApplicationMiddleware(WebApplicationTerminal.InvokeAsync);

        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            middleware = _middleware[i].Invoke(middleware);
        }

        // Seed every application-registered feature (IWebApplicationBuilder.AddFeature, e.g.
        // routing's per-application IRouterFeature) onto each exchange before any user
        // middleware runs. The feature set is snapshotted once here, at pipeline build — per
        // the hosting philosophy, DI integration is builder-time only, so no service is
        // resolved per request.
        IHttpFeature[] features = Context.ServiceProvider.GetRequiredService<IEnumerable<IHttpFeature>>().ToArray();

        // Owner decision 35 (#1380): an exchange disposes every disposable feature it carries when it
        // ends, so a disposable application feature, stamped as one shared instance, would be disposed
        // after its first request and handed to every later one. Builder-time checks cannot see this:
        // a factory's product is known only once it is resolved, which is here.
        foreach (IHttpFeature feature in features)
        {
            if (feature is IDisposable or IAsyncDisposable)
            {
                throw new InvalidOperationException(
                    $"The application feature '{feature.Name}' ({feature.GetType().FullName}) is disposable. " +
                    "The host stamps the same feature instance onto every exchange, and an exchange disposes the " +
                    "disposable features it carries when it ends, so this instance would be disposed after its " +
                    "first request. Keep disposable per-request state in a feature that middleware installs on " +
                    "each exchange.");
            }
        }

        if (features.Length > 0)
        {
            WebApplicationMiddleware pipeline = middleware;

            middleware = new WebApplicationMiddleware(context =>
            {
                foreach (IHttpFeature feature in features)
                {
                    context.Features.Set(feature);
                }

                return pipeline.Invoke(context);
            });
        }

        _isBuilt = true;
        return new WebApplicationPipeline(middleware);
    }

    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        return ((IWebApplicationPipelineBuilder)this).Use((WebApplicationMiddleware next) =>
        {
            return middleware.Invoke(Context, next);
        });
    }
    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware.Add(middleware);

        return this;
    }
    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(IWebApplicationMiddleware middleware)
    {
        return (IWebApplicationPipelineBuilder)this.Use(middleware.InvokeAsync);
    }
    Task IWebApplication.StartAsync(CancellationToken cancellationToken)
    {
        return (this as IHost).StartAsync(cancellationToken);
    }
    Task IWebApplication.StopAsync(CancellationToken cancellationToken)
    {
        return (this as IHost).StopAsync(cancellationToken);
    }

    public static WebApplicationBuilder CreateBuilder()
    {
        return CreateBuilder(new WebApplicationOptions()
        {

        });
    }

    /// <summary>
    /// Creates a Web application builder that honors an enabled resource's generated
    /// control-plane registration and ambient invocation context.
    /// </summary>
    /// <param name="args">The application command-line arguments.</param>
    /// <returns>A new Web application builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(WebApplication).Assembly;
        return CreateBuilder(args, resourceAssembly);
    }

    internal static WebApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        return new WebApplicationBuilder(new WebApplicationOptions(), resourceAssembly, args);
    }

    public static WebApplicationBuilder CreateBuilder(WebApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new WebApplicationBuilder(options);
    }
}
