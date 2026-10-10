using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A server whose start and stop cores record their calls (and can fail), over the engine the base
/// fixes.
/// </summary>
internal sealed class TestServer : DatabaseServer
{
    private readonly List<DatabaseServerSession> _sessions = [];
    private readonly string _name;
    private int _starts;
    private int _stops;

    public TestServer(DatabaseEngine engine, TestLog? log = null, string name = "server")
        : base(engine)
    {
        _name = name;
        Log = log ?? new TestLog();
    }

    public TestLog Log { get; }

    public int Starts => Volatile.Read(ref _starts);

    public int Stops => Volatile.Read(ref _stops);

    /// <summary>Gets or sets a failure the start core throws.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Gets or sets a failure the stop core throws.</summary>
    public Exception? StopFailure { get; set; }

    public bool Running => IsRunning;

    public override IReadOnlyCollection<DatabaseServerSession> Sessions => _sessions.AsReadOnly();

    protected override Task StartCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _starts);
        Log.Add($"{_name}:start");
        return StartFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    protected override Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stops);
        Log.Add($"{_name}:stop");
        return StopFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}
