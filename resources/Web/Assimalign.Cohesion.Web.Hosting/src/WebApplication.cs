using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
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

[global::System.Diagnostics.DebuggerDisplay("WebApplication {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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
        List<Exception>? failures = null;
        try
        {
            await base.DisposeAsync(disposing).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures = new List<Exception> { exception };
        }

        if (disposing)
        {
            // Stop failures are reported after the host has attempted every service. Final teardown
            // must still release factory-owned services, middleware lifetimes and endpoint keys.
            try
            {
                await _context.DisposeServiceProviderAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }

            foreach (X509Certificate2 certificate in _context.EndpointCertificates)
            {
                try
                {
                    certificate.Dispose();
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
            _context.EndpointCertificates.Clear();
        }

        // A failed StopAsync leaves the base disposal flag unset. All final releases have now
        // been attempted, so seal the host without retrying stop or allowing a later restart.
        await base.DisposeAsync(disposing: false).ConfigureAwait(false);

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures is not null)
        {
            throw new AggregateException(ErrorMessages.ApplicationTeardownFailed, failures);
        }
    }

    /// <summary>
    /// Adds a middleware component to the pipeline.
    /// </summary>
    /// <param name="middleware">The middleware invoked for each request.</param>
    /// <returns>The same application for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="middleware"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Middleware cannot be registered after the pipeline is built.
    /// </exception>
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
        // after its first request and handed to every later one. WebApplicationBuilder.Build already
        // rejected the registrations that carry their product's type (an instance or an implementation
        // type); a factory's product is known only once it is resolved, which is here.
        foreach (IHttpFeature feature in features)
        {
            if (feature is IDisposable or IAsyncDisposable)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationFeatureDisposable, feature.Name, feature.GetType().FullName));
            }
        }

        // The default server sizes each exchange's feature collection for these, so stamping them
        // never grows it.
        _context.StampedFeatureCount = features.Length;

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

        InvalidOperationException.ThrowIf(_isBuilt, "Middleware cannot be added because the web pipeline has already been built.");

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

    private sealed class DebugView
    {
        private readonly WebApplication _target;

        public DebugView(WebApplication value)
        {
            _target = value;
        }

        public WebApplicationContext Context => _target.Context;
        public HostId Id => _target.Id;
    }
}
