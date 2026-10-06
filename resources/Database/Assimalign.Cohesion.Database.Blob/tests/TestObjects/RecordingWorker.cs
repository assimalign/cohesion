using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// A worker a builder factory composes into a blob engine: it records its passes, the name of
/// the thread its pump runs it on, and its disposal, and derives from the root base, as every
/// worker the engine can attach does.
/// </summary>
internal sealed class RecordingWorker : DatabaseEngineWorker
{
    private readonly ManualResetEventSlim _started = new();
    private string? _threadName;
    private int _passes;
    private int _disposals;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingWorker"/> class.
    /// </summary>
    /// <param name="engine">The engine the worker was created for.</param>
    /// <param name="name">The worker's name; <c>{engine}/recording</c> when null.</param>
    public RecordingWorker(BlobDatabaseEngine engine, string? name = null)
        : base(name ?? engine.Name + "/recording", DatabaseEngineWorkerKind.IndexMaintenance, TimeSpan.FromMilliseconds(10))
    {
        Engine = engine;
    }

    /// <summary>Gets the engine the worker was created for.</summary>
    public BlobDatabaseEngine Engine { get; }

    /// <summary>Gets whether a pass ran.</summary>
    public ManualResetEventSlim Started => _started;

    /// <summary>Gets the name of the thread the first pass ran on, or null before it ran.</summary>
    public string? ThreadName => Volatile.Read(ref _threadName);

    /// <summary>Gets the number of passes that ran.</summary>
    public int Passes => Volatile.Read(ref _passes);

    /// <summary>Gets the number of times the worker was disposed.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    // The base's release hook, which the owning engine runs once, or DisposeAsync for a worker no
    // engine owns (one a builder refused).
    protected override ValueTask DisposeAsyncCore()
    {
        Interlocked.Increment(ref _disposals);
        return ValueTask.CompletedTask;
    }

    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        Interlocked.CompareExchange(ref _threadName, Thread.CurrentThread.Name, null);
        Interlocked.Increment(ref _passes);
        _started.Set();
    }
}
