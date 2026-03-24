
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Hosting;

public class ScheduleApplicationBuilder : ISchedulerBuilder
{
    public ScheduleApplicationBuilder()
    {
        
    }

    /// <summary>
    /// 
    /// </summary>
    public HostEnvironment Environment { get; }

    /// <summary>
    /// 
    /// </summary>
    //public WebApplicationServerBuilder Web { get; }

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
    public FileSystemFactoryBuilder FileSystems { get; }

    public IScheduleProvider AddScheduleProvider(IScheduleProvider provider)
    {
        throw new NotImplementedException();
    }

    ISchedulerBuilder ISchedulerBuilder.AddSchedule(ISchedule schedule)
    {
        throw new NotImplementedException();
    }

    async Task<IScheduler> ISchedulerBuilder.BuildAsync()
    {
        IServiceProvider serviceProvider = (Services as IServiceProviderBuilder).Build();

        return new ScheduleApplication(default);
    }
}
