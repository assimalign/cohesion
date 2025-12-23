using System;
using System.IO;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Scheduler;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.DependencyInjection;

public class ScheduleContext : HostContext
{
    private readonly ServiceProviderBuilder _builder;
    internal ScheduleContext(ServiceProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _builder = builder;
    }


    public override FileSystemPath? ContentRootPath { get; }
    public override IServiceProvider ServiceProvider => (_builder as IServiceProviderBuilder).Build();
    public override IHostEnvironment Environment => ServiceProvider.GetRequiredService<IHostEnvironment>();
    public override IEnumerable<IHostService> HostedServices => ServiceProvider.GetRequiredService<IEnumerable<IHostService>>();
}