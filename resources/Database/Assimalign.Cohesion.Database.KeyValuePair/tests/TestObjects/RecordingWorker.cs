using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// A worker a builder factory composes into a key-value engine: it records its passes and its
/// disposal, and derives from the root base, as every worker the engine can attach does.
/// </summary>
internal sealed class RecordingWorker : DatabaseEngineWorker, IDisposable
{
    private readonly ManualResetEventSlim _started = new();
    private int _passes;
    private int _disposals;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingWorker"/> class.
    /// </summary>
    /// <param name="engine">The engine the worker was created for.</param>
    /// <param name="name">The worker's name; <c>{engine}/recording</c> when null.</param>
    public RecordingWorker(KeyValueDatabaseEngine engine, string? name = null)
        : base(name ?? engine.Name + "/recording", DatabaseEngineWorkerKind.IndexMaintenance, TimeSpan.FromMilliseconds(10))
    {
        Engine = engine;
    }

    /// <summary>Gets the engine the worker was created for.</summary>
    public KeyValueDatabaseEngine Engine { get; }

    /// <summary>Gets whether a pass ran.</summary>
    public ManualResetEventSlim Started => _started;

    /// <summary>Gets the number of passes that ran.</summary>
    public int Passes => Volatile.Read(ref _passes);

    /// <summary>Gets the number of times the worker was disposed.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    public void Dispose() => Interlocked.Increment(ref _disposals);

    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _passes);
        _started.Set();
    }
}
