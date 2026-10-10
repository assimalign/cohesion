using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A server of the root base that records its start and stop cores into a shared log, so tests
/// can assert start/stop ordering relative to other services.
/// </summary>
/// <remarks>
/// The base owns the lifecycle (<c>database-area.md</c>, "Test doubles"): a start that fails
/// leaves the server stopped for good, a stop is terminal and idempotent, and disposal is a stop,
/// so the stop core runs once, for the host's stop or for the owning engine's disposal of the
/// server, whichever comes first, and never after a failed start.
/// </remarks>
internal sealed class RecordingServer : DatabaseServer
{
    private readonly List<string> _log;
    private readonly string _name;
    private int _stops;

    public RecordingServer(List<string> log, string name = "server", DatabaseEngine? engine = null)
        : base(engine ?? new RecordingEngine())
    {
        _log = log;
        _name = name;
    }

    /// <inheritdoc />
    public override IReadOnlyCollection<DatabaseServerSession> Sessions => [];

    /// <summary>
    /// Gets how many times the base ran the stop core: one once the server was stopped or
    /// disposed, zero while it was neither.
    /// </summary>
    internal int Stops => Volatile.Read(ref _stops);

    internal Exception? StartException { get; set; }

    internal Exception? StopException { get; set; }

    protected override Task StartCoreAsync(CancellationToken cancellationToken)
    {
        _log.Add($"{_name}:start");
        return StartException is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    protected override Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stops);
        _log.Add($"{_name}:stop");
        return StopException is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}
