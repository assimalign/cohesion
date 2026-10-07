using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// A worker of the root base whose passes the test runs: its pump waits for its engine's disposal
/// and never runs a pass on its own, and a pass fails as a whole while <see cref="Failure"/> is
/// set, so the worker holds a fault (and its engine reports <see cref="EngineState.Faulted"/>)
/// exactly between a failed pass the test ran and the next pass that runs to its end.
/// </summary>
internal sealed class RecordingEngineWorker : DatabaseEngineWorker
{
    internal RecordingEngineWorker(
        string name,
        DatabaseEngineWorkerKind kind,
        TimeSpan interval)
        : base(name, kind, interval)
    {
    }

    /// <summary>Gets or sets the failure the next pass throws; null for a pass that succeeds.</summary>
    internal Exception? Failure { get; set; }

    protected override void WaitForTrigger(CancellationToken cancellationToken)
        => cancellationToken.WaitHandle.WaitOne();

    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}
