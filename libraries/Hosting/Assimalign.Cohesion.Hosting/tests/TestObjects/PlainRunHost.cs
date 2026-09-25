using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;

internal sealed class PlainRunHost : IHost
{
    internal List<string> Events { get; } = [];
    public TestHostContext Context { get; } = new([]);
    IHostContext IHost.Context => Context;
    public HostId Id => Context.HostId;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add("Start");
        Context.SetState(HostState.Started);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add("Stop");
        Context.SetState(HostState.Stopped);
        return Task.CompletedTask;
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
