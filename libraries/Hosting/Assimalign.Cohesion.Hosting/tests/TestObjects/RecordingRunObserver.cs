using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;

internal sealed class RecordingRunObserver : IHostRunner, IHostRunObserver
{
    internal List<string> Events { get; } = [];

    public Task RunAsync(IHostRun run, CancellationToken cancellationToken = default) =>
        run.RunAsync(this, cancellationToken);

    public void Started(IHost host) => Events.Add("Started");
    public void Stopping(IHost host) => Events.Add("Stopping");
    public void Stopped(IHost host) => Events.Add("Stopped");
    public void DrainAborted(IHost host) => Events.Add("DrainAborted");
}
