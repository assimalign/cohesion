using System.IO;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Scheduler.Hosting;

internal sealed class SchedulerApplicationOptions : HostOptions<SchedulerApplicationContext>
{
    internal FileSystemPath? ContentRootPath { get; init; }
}
