using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A guided worker that records when its pump stops and when it is disposed, so a test can assert
/// that an engine stops every pump before it disposes any worker.
/// </summary>
internal sealed class RecordingWorker : DatabaseEngineWorker, IDisposable
{
    private readonly TestLog _log;
    private int _waits;
    private int _disposes;

    public RecordingWorker(TestLog log, string name)
        : base(name, DatabaseEngineWorkerKind.IndexMaintenance, TimeSpan.FromHours(1))
    {
        _log = log;
    }

    /// <summary>Gets or sets a failure the disposal throws after it was recorded.</summary>
    public Exception? DisposeFailure { get; set; }

    /// <summary>Gets a signal set once the pump's first trigger wait started.</summary>
    public ManualResetEventSlim Waiting { get; } = new();

    /// <summary>Gets how many times the worker was disposed.</summary>
    public int Disposes => Volatile.Read(ref _disposes);

    public void Dispose()
    {
        Interlocked.Increment(ref _disposes);
        _log.Add($"{Name}:dispose");
        if (DisposeFailure is { } failure)
        {
            throw failure;
        }
    }

    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
    }

    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _waits) == 1)
        {
            Waiting.Set();
        }

        cancellationToken.WaitHandle.WaitOne(Interval);
        if (cancellationToken.IsCancellationRequested)
        {
            _log.Add($"{Name}:stopped");
        }
    }
}
