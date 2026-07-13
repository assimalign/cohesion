using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A controllable engine worker for host-mapping tests: signals when its blocking
/// pump is entered and exited and when a timer-driven iteration runs, so tests can
/// assert the host actually drives claimed workers without real work or sleeps.
/// </summary>
internal sealed class RecordingWorker : DatabaseEngineWorker
{
    private readonly DatabaseEngineWorkerKind _kind;
    private readonly TimeSpan _interval;

    public RecordingWorker(DatabaseEngineWorkerKind kind, TimeSpan interval)
    {
        _kind = kind;
        _interval = interval;
    }

    /// <summary>Set when the blocking pump (<see cref="DatabaseEngineWorker.Run"/>) is entered.</summary>
    public ManualResetEventSlim RunEntered { get; } = new();

    /// <summary>Set when the blocking pump exits.</summary>
    public ManualResetEventSlim RunExited { get; } = new();

    /// <summary>Set when a pump pass (<see cref="RunIteration"/>) executes.</summary>
    public ManualResetEventSlim IterationRan { get; } = new();

    public override string Name => $"recording/{_kind}";

    public override DatabaseEngineWorkerKind Kind => _kind;

    public override TimeSpan Interval => _interval;

    public override void Run(CancellationToken cancellationToken)
    {
        RunEntered.Set();

        try
        {
            base.Run(cancellationToken);
        }
        finally
        {
            RunExited.Set();
        }
    }

    public override void RunIteration(CancellationToken cancellationToken)
    {
        IterationRan.Set();
    }
}
