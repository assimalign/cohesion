using System.IO;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Scheduler.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("SchedulerApplicationOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SchedulerApplicationOptions : HostOptions<SchedulerApplicationContext>
{
    internal FileSystemPath? ContentRootPath { get; init; }

    private sealed class DebugView
    {
        private readonly SchedulerApplicationOptions _target;

        public DebugView(SchedulerApplicationOptions value)
        {
            _target = value;
        }

        public bool StartServicesConcurrently => _target.StartServicesConcurrently;
        public bool StopServicesConcurrently => _target.StopServicesConcurrently;
        public global::System.TimeSpan StartupTimeout => _target.StartupTimeout;
        public global::System.TimeSpan ShutdownTimeout => _target.ShutdownTimeout;
        public string? Environment => _target.Environment;
    }
}
