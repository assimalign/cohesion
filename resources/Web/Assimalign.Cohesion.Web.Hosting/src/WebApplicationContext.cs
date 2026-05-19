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
    private readonly ServiceProviderBuilder _builder;
    internal WebApplicationContext(ServiceProviderBuilder builder)
    {
        _builder = ArgumentNullException.ThrowIfNull<ServiceProviderBuilder>(builder);
    }
    public FileSystemPath? ContentRootPath { get; }
    public override IServiceProvider ServiceProvider => (_builder as IServiceProviderBuilder).Build();
    public override IHostEnvironment Environment => ServiceProvider.GetRequiredService<IHostEnvironment>();
    public override IEnumerable<IHostService> HostedServices => ServiceProvider.GetRequiredService<IEnumerable<IHostService>>();
    public IEnumerable<IWebApplicationServer> Servers => HostedServices.OfType<IWebApplicationServer>();
    public IEnumerable<IWebApplicationMiddleware> Middleware => ServiceProvider.GetRequiredService<IEnumerable<IWebApplicationMiddleware>>();
    public IEnumerable<IHttpFeature> Features => ServiceProvider.GetRequiredService<IEnumerable<IHttpFeature>>();
}
