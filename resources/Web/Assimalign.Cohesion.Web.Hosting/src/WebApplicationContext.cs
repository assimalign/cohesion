using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.DependencyInjection;

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
    public IEnumerable<IWebApplicationServer> Servers => HostedServices.OfType<IWebApplicationServer>();
    public override IEnumerable<IHostService> HostedServices => ServiceProvider.GetRequiredService<IEnumerable<IHostService>>();
    public IWebApplicationPipeline Pipeline => ServiceProvider.GetRequiredService<IWebApplicationPipeline>();
}
