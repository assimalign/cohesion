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
    private readonly string _name;
    private readonly RecordingServerContext _context;

    public RecordingServer(List<string> log, string name = "server", IDatabaseEngine? engine = null)
    {
        _log = log;
        _name = name;
        _context = new RecordingServerContext(engine ?? new RecordingEngine());
    }

    public IDatabaseServerContext Context => _context;

    internal int DisposeCount { get; private set; }
    internal Exception? StartException { get; set; }
    internal Exception? StopException { get; set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _log.Add($"{_name}:start");
        if (StartException is not null)
        {
            throw StartException;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _log.Add($"{_name}:stop");
        if (StopException is not null)
        {
            throw StopException;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    private sealed class RecordingServerContext : IDatabaseServerContext
    {
        internal RecordingServerContext(IDatabaseEngine engine)
        {
            Engine = engine;
        }

        public IDatabaseEngine Engine { get; }

        public IReadOnlyCollection<IDatabaseServerSession> Sessions => Array.Empty<IDatabaseServerSession>();
    }
}
