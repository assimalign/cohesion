using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Hosting;

internal sealed class SchedulerApplicationBuilder : ISchedulerApplicationBuilder
{
    internal SchedulerApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public ISchedulerApplication Build()
    {
        var options = new SchedulerApplicationOptions();
        var context = new SchedulerApplicationContext();

        return new SchedulerApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
