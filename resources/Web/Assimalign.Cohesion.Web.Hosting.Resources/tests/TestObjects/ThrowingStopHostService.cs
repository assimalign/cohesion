using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

internal sealed class ThrowingStopHostService : IHostService, IDisposable
{
    private readonly Exception _failure;

    internal ThrowingStopHostService(Exception failure)
    {
        _failure = failure;
    }

    public ServiceId Id { get; } = ServiceId.New();

    internal int DisposeCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.FromException(_failure);

    public void Dispose() => DisposeCount++;
}
