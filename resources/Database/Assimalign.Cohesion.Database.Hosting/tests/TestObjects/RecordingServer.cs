using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A minimal <see cref="IDatabaseServer"/> that records its lifecycle calls into a
/// shared log so tests can assert start/stop ordering relative to other services.
/// </summary>
internal sealed class RecordingServer : IDatabaseServer
{
    private readonly List<string> _log;

    public RecordingServer(List<string> log)
    {
        _log = log;
    }

    public IReadOnlyList<IDatabaseEngine> Engines => Array.Empty<IDatabaseEngine>();

    public IReadOnlyCollection<IDatabaseServerSession> Sessions => Array.Empty<IDatabaseServerSession>();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _log.Add("server:start");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _log.Add("server:stop");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
