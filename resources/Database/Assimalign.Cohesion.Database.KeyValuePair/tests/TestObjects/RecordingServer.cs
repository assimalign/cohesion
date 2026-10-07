using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// A server a builder factory composes into an engine: it records its start and stop cores, over
/// the engine the root base fixes. The stop core runs once whatever ends the server (a stop or a
/// disposal), so its count is the number of times the server was released.
/// </summary>
internal sealed class RecordingServer : DatabaseServer
{
    private int _starts;
    private int _stops;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingServer"/> class.
    /// </summary>
    /// <param name="engine">The engine the server fronts.</param>
    public RecordingServer(DatabaseEngine engine)
        : base(engine)
    {
    }

    /// <summary>Gets the number of times the start core ran.</summary>
    public int Starts => Volatile.Read(ref _starts);

    /// <summary>Gets the number of times the stop core ran.</summary>
    public int Stops => Volatile.Read(ref _stops);

    /// <summary>Gets or sets a failure the stop core throws after it counted the stop.</summary>
    public Exception? StopFailure { get; set; }

    public override IReadOnlyCollection<DatabaseServerSession> Sessions => [];

    protected override Task StartCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _starts);
        return Task.CompletedTask;
    }

    protected override Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stops);
        return StopFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}
