using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;

internal sealed class HookRecordingHost : Host<TestHostContext>
{
    private readonly Action<string> _record;

    public HookRecordingHost(TestHostOptions options, Action<string> record) : base(options)
    {
        _record = record;
        Context = new TestHostContext(options.HostedServices);
    }

    public override TestHostContext Context { get; }

    protected override Task OnStartingAsync(CancellationToken cancellationToken = default)
    {
        _record("OnStarting");
        return Task.CompletedTask;
    }

    protected override Task OnStartedAsync(CancellationToken cancellationToken = default)
    {
        _record("OnStarted");
        return Task.CompletedTask;
    }

    protected override Task OnStoppingAsync(CancellationToken cancellationToken = default)
    {
        _record("OnStopping");
        return Task.CompletedTask;
    }

    // Deliberately no base call: restart correctness must not depend on it.
    protected override Task OnStoppedAsync(CancellationToken cancellationToken = default)
    {
        _record("OnStopped");
        return Task.CompletedTask;
    }
}
