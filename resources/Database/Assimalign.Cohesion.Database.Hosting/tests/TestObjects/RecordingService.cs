using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A minimal <see cref="IHostService"/> that records its lifecycle calls into a
/// shared log so tests can assert that additional services start before the
/// servers and stop after they drain.
/// </summary>
internal sealed class RecordingService : IHostService
{
    private readonly List<string> _log;
    private readonly string _name;

    public RecordingService(List<string> log, string name = "service")
    {
        _log = log;
        _name = name;
        Id = ServiceId.New();
    }

    public ServiceId Id { get; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _log.Add($"{_name}:start");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _log.Add($"{_name}:stop");
        return Task.CompletedTask;
    }
}
