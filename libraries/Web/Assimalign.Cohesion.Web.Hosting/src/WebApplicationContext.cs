using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Internal;

public sealed class WebApplicationContext : HostContext
{
    private readonly ServiceProviderBuilder _builder;
    internal WebApplicationContext(ServiceProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _builder = builder;
    }
    public override FileSystemPath? ContentRootPath { get; }
    public override IServiceProvider ServiceProvider => (_builder as IServiceProviderBuilder).Build();
    public override IHostEnvironment Environment => ServiceProvider.GetRequiredService<IHostEnvironment>();
    public override IEnumerable<IHostService> HostedServices => ServiceProvider.GetRequiredService<IEnumerable<IHostService>>();
}
