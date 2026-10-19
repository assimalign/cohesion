using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A database whose cores create <see cref="TestSession"/>s and record their disposal. A close
/// gate, when given, holds the disposal cores until it is released, so a test can act while a
/// close runs.
/// </summary>
internal sealed class TestDatabase : DatabaseInstance
{
    private readonly TaskCompletionSource? _closeGate;
    private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sessions;
    private int _disposeCores;
    private int _asyncDisposeCores;

    public TestDatabase(DatabaseName name, DatabaseEngine engine, TestLog? log = null, TaskCompletionSource? closeGate = null)
        : base(name, engine)
    {
        Log = log ?? new TestLog();
        _closeGate = closeGate;
    }

    public TestLog Log { get; }

    public int Sessions => Volatile.Read(ref _sessions);

    public int DisposeCores => Volatile.Read(ref _disposeCores);

    public int AsyncDisposeCores => Volatile.Read(ref _asyncDisposeCores);

    public bool Disposed => IsDisposed;

    /// <summary>Gets a task that completes once a disposal core started.</summary>
    public Task Closing => _closing.Task;

    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sessions);
        return new ValueTask<DatabaseSession>(new TestSession(this, Log));
    }

    protected override void DisposeCore()
    {
        Interlocked.Increment(ref _disposeCores);
        _closing.TrySetResult();
        _closeGate?.Task.GetAwaiter().GetResult();
        Log.Add($"database:{Name}:dispose");
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        Interlocked.Increment(ref _asyncDisposeCores);
        _closing.TrySetResult();
        if (_closeGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        Log.Add($"database:{Name}:dispose");
    }
}
