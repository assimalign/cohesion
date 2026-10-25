using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>A host run that nothing drives; runner decorators only pass it through.</summary>
internal sealed class TestHostRun : IHostRun
{
    public TestHostRun(IHost host)
    {
        Host = host;
    }

    public IHost Host { get; }

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public Task RunAsync(IHostRunObserver? observer, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public bool TryShutdown(Action? onAccepted = null)
    {
        onAccepted?.Invoke();
        return true;
    }
}
