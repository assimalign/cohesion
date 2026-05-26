using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;

public sealed class WebApplicationContext : HostContext, IWebApplicationContext
{
    private readonly Lazy<IServiceProvider> _serviceProvider;
    internal WebApplicationContext(ServiceProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _serviceProvider = new Lazy<IServiceProvider>(() => 
        {
            return (builder as IServiceProviderBuilder).Build();
        });
    }
    public FileSystemPath? ContentRootPath { get; init; }
    public IServiceProvider ServiceProvider => _serviceProvider.Value;
    public override IHostEnvironment Environment => ServiceProvider.GetRequiredService<IHostEnvironment>();
    public override IEnumerable<IHostService> HostedServices => ServiceProvider.GetRequiredService<IEnumerable<IHostService>>();
    public IEnumerable<IWebApplicationServer> Servers => HostedServices.OfType<IWebApplicationServer>();
    public IEnumerable<IWebApplicationMiddleware> Middleware => ServiceProvider.GetRequiredService<IEnumerable<IWebApplicationMiddleware>>();
    public IEnumerable<IHttpFeature> Features => ServiceProvider.GetRequiredService<IEnumerable<IHttpFeature>>();
}
